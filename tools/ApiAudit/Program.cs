// Verifies that every type and member the mod references from the runtime
// libraries actually exists in the game's own (Unity Mono 2.0) assemblies.
// Building against Microsoft's .NET 3.5 reference assemblies is standard for
// MSCLoader mods, but Unity's Mono profile omits a few APIs; a reference to
// one of those compiles fine and then throws TypeLoadException/MissingMethod
// inside the game. This audit catches that at build time.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

static class Program
{
    sealed class AsmIndex
    {
        public readonly Dictionary<string, TypeInfo> Types = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
    }
    sealed class TypeInfo
    {
        public string Name;
        public string BaseType;
        public readonly HashSet<string> Members = new HashSet<string>(StringComparer.Ordinal); // name/arity
        public readonly HashSet<string> MemberNames = new HashSet<string>(StringComparer.Ordinal);
    }

    static readonly Dictionary<string, AsmIndex> Indexes = new Dictionary<string, AsmIndex>(StringComparer.OrdinalIgnoreCase);

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: ApiAudit <mod.dll> <game Managed dir>"); return 2; }
        string modPath = args[0], managed = args[1];
        foreach (var file in Directory.GetFiles(managed, "*.dll"))
        {
            try { IndexAssembly(file); } catch (Exception e) { Console.Error.WriteLine("skip " + Path.GetFileName(file) + ": " + e.Message); }
        }
        int problems = 0, checkedRefs = 0;
        using var stream = File.OpenRead(modPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        foreach (var h in md.TypeReferences)
        {
            var tr = md.GetTypeReference(h);
            string full = FullName(md, tr);
            string scope = ScopeAssembly(md, tr.ResolutionScope);
            if (scope == null) continue; // nested handled via parent
            checkedRefs++;
            if (!ResolveType(scope, full, out _))
            {
                Console.WriteLine("MISSING TYPE  " + scope + " :: " + full);
                problems++;
            }
        }
        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue; // generic instantiations etc. checked loosely below
            var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
            string scope = ScopeAssembly(md, tr.ResolutionScope);
            if (scope == null) continue;
            string full = FullName(md, tr);
            string name = md.GetString(mr.Name);
            checkedRefs++;
            if (!ResolveType(scope, full, out var ti)) continue; // already reported
            int arity = MemberArity(md, mr);
            if (!HasMember(scope, ti, name, arity))
            {
                Console.WriteLine("MISSING MEMBER " + scope + " :: " + full + "." + name + "/" + arity);
                problems++;
            }
        }
        // Member refs on generic instantiations (e.g. List<T>.Add)
        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            if (mr.Parent.Kind != HandleKind.TypeSpecification) continue;
            var ts = md.GetTypeSpecification((TypeSpecificationHandle)mr.Parent);
            var blob = md.GetBlobReader(ts.Signature);
            var header = blob.ReadSignatureTypeCode();
            if (header != SignatureTypeCode.GenericTypeInstance) continue;
            blob.ReadSignatureTypeCode();
            var handle = blob.ReadTypeHandle();
            if (handle.Kind != HandleKind.TypeReference) continue;
            var tr = md.GetTypeReference((TypeReferenceHandle)handle);
            string scope = ScopeAssembly(md, tr.ResolutionScope);
            if (scope == null) continue;
            string full = FullName(md, tr);
            string name = md.GetString(mr.Name);
            checkedRefs++;
            if (!ResolveType(scope, full, out var ti)) continue;
            int arity = MemberArity(md, mr);
            if (!HasMember(scope, ti, name, arity))
            {
                Console.WriteLine("MISSING MEMBER " + scope + " :: " + full + "." + name + "/" + arity);
                problems++;
            }
        }
        Console.WriteLine("API audit: " + checkedRefs + " references checked, " + problems + " problem(s).");
        return problems == 0 ? 0 : 1;
    }

    static bool HasMember(string scope, TypeInfo ti, string name, int arity)
    {
        var seen = new HashSet<string>();
        var cur = ti;
        while (cur != null && seen.Add(cur.Name))
        {
            if (arity < 0 ? cur.MemberNames.Contains(name) : (cur.Members.Contains(name + "/" + arity) || (name == ".ctor" && false)))
                return true;
            if (arity >= 0 && cur.MemberNames.Contains(name) && (name.StartsWith("get_") || name.StartsWith("set_"))) return true;
            if (cur.BaseType == null) break;
            if (!ResolveAny(cur.BaseType, out cur)) break;
        }
        return false;
    }

    static bool ResolveAny(string full, out TypeInfo ti)
    {
        foreach (var idx in Indexes.Values)
            if (idx.Types.TryGetValue(full, out ti)) return true;
        ti = null; return false;
    }

    static bool ResolveType(string scope, string full, out TypeInfo ti)
    {
        // Mono/Unity may place a type in a different assembly than Microsoft's reference
        // assemblies do (type forwarding is not used in this old profile), so accept any
        // game assembly that defines it.
        if (Indexes.TryGetValue(scope, out var idx) && idx.Types.TryGetValue(full, out ti)) return true;
        return ResolveAny(full, out ti);
    }

    static int MemberArity(MetadataReader md, MemberReference mr)
    {
        var blob = md.GetBlobReader(mr.Signature);
        var header = blob.ReadSignatureHeader();
        if (header.Kind == SignatureKind.Field) return -1;
        if (header.IsGeneric) blob.ReadCompressedInteger();
        return blob.ReadCompressedInteger();
    }

    static string ScopeAssembly(MetadataReader md, EntityHandle scope)
    {
        if (scope.Kind == HandleKind.AssemblyReference)
            return md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
        if (scope.Kind == HandleKind.TypeReference)
            return ScopeAssembly(md, md.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope);
        return null;
    }

    static string FullName(MetadataReader md, TypeReference tr)
    {
        string name = md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return FullName(md, md.GetTypeReference((TypeReferenceHandle)tr.ResolutionScope)) + "/" + name;
        string ns = md.GetString(tr.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    static void IndexAssembly(string file)
    {
        using var stream = File.OpenRead(file);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return;
        var md = pe.GetMetadataReader();
        if (!md.IsAssembly) return;
        string asmName = md.GetString(md.GetAssemblyDefinition().Name);
        var idx = new AsmIndex();
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            string full = DefFullName(md, td);
            var ti = new TypeInfo { Name = full };
            if (!td.BaseType.IsNil) ti.BaseType = BaseName(md, td.BaseType);
            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                string n = md.GetString(m.Name);
                var blob = md.GetBlobReader(m.Signature);
                var header = blob.ReadSignatureHeader();
                if (header.IsGeneric) blob.ReadCompressedInteger();
                int pc = blob.ReadCompressedInteger();
                ti.Members.Add(n + "/" + pc);
                ti.MemberNames.Add(n);
            }
            foreach (var fh in td.GetFields())
            {
                string n = md.GetString(md.GetFieldDefinition(fh).Name);
                ti.MemberNames.Add(n);
            }
            idx.Types[full] = ti;
        }
        // exported (forwarded) types are not used in this profile
        Indexes[asmName] = idx;
    }

    static string BaseName(MetadataReader md, EntityHandle h)
    {
        if (h.Kind == HandleKind.TypeDefinition) return DefFullName(md, md.GetTypeDefinition((TypeDefinitionHandle)h));
        if (h.Kind == HandleKind.TypeReference) return FullName(md, md.GetTypeReference((TypeReferenceHandle)h));
        if (h.Kind == HandleKind.TypeSpecification)
        {
            var ts = md.GetTypeSpecification((TypeSpecificationHandle)h);
            var blob = md.GetBlobReader(ts.Signature);
            if (blob.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance)
            {
                blob.ReadSignatureTypeCode();
                return BaseName(md, blob.ReadTypeHandle());
            }
        }
        return null;
    }

    static string DefFullName(MetadataReader md, TypeDefinition td)
    {
        string name = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return DefFullName(md, md.GetTypeDefinition(decl)) + "/" + name;
        string ns = md.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }
}
