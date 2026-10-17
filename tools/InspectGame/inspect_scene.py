"""Read-only metadata inspection of a user's installed Unity 5 game.

Dependencies (local tooling only): UnityPy==1.25.4 and
TypeTreeGeneratorAPI==0.0.10 in .local/inspect-python. Game code/assets are not
copied or distributed. Output is ignored evidence, not a generated adapter.
"""
import argparse
import collections
import hashlib
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.local' / 'inspect-python'))
import UnityPy
from UnityPy.helpers import TypeTreeHelper
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
# The boost parser does not honor the normalized old MonoBehaviour header.
# The pure reader validates byte-for-byte consumption for the legacy schema.
TypeTreeHelper.read_typetree_boost = None


def legacy_node(node):
    """Normalize generator 0.0.10 nodes to UnityPy's legacy reader contract.

    The generator emits primitive arrays with scalar names (e.g. string[] as
    string), which UnityPy interprets as scalar. Preserve actual string/char
    arrays, relabel container nodes, and align the MonoBehaviour header byte.
    read_typetree still requires the exact serialized byte count to match.
    """
    if node.m_Name == 'm_Enabled':
        node.m_MetaFlag |= 0x4000
    if (node.m_Children and node.m_Children[0].m_Type == 'Array'
            and node.m_Type in TypeTreeHelper.FUNCTION_READ_MAP):
        element = node.m_Children[0].m_Children[1]
        if not (node.m_Type == 'string' and element.m_Type == 'char'):
            node.m_Type = 'vector'
    for child in node.m_Children:
        legacy_node(child)
    return node


def action_summary(data, index, action_name):
    begin = data['actionStartIndex'][index]
    end = data['actionStartIndex'][index + 1] if index + 1 < len(data['actionStartIndex']) else len(data['paramName'])
    values = {}
    tables = {18: 'fsmStringParams', 19: 'fsmGameObjectParams', 20: 'fsmOwnerDefaultParams'}
    for field in range(begin, end):
        kind = data['paramDataType'][field]
        position = data['paramDataPos'][field]
        if kind in tables and 0 <= position < len(data.get(tables[kind], [])):
            values[data['paramName'][field]] = data[tables[kind]][position]
    return {'type': action_name, 'enabled': bool(data['actionEnabled'][index]), 'parameters': values}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--game', required=True, type=pathlib.Path)
    parser.add_argument('--scene', default='level2')
    parser.add_argument('--output', type=pathlib.Path)
    args = parser.parse_args()
    scene = args.game / 'mysummercar_Data' / args.scene
    env = UnityPy.load(str(scene))
    env.typetree_generator = TypeTreeGenerator('5.0.0f4')
    env.typetree_generator.load_local_game(str(args.game))
    objects = list(env.objects)
    gameobjects = {o.path_id: o.read_typetree() for o in objects if o.type.name == 'GameObject'}
    transforms = {o.path_id: o.read_typetree() for o in objects if o.type.name == 'Transform'}
    transform_by_go = {t['m_GameObject']['m_PathID']: t for t in transforms.values()}
    paths = {}

    def object_path(go_id):
        if go_id in paths:
            return paths[go_id]
        original_id = go_id
        parts, visited = [], set()
        while go_id and go_id in gameobjects:
            if go_id in visited:
                raise ValueError('Transform cycle in serialized scene')
            visited.add(go_id)
            parts.append(gameobjects[go_id]['m_Name'])
            parent = transform_by_go.get(go_id, {}).get('m_Father', {}).get('m_PathID', 0)
            go_id = transforms.get(parent, {}).get('m_GameObject', {}).get('m_PathID', 0)
        result = '/'.join(reversed(parts))
        paths[original_id] = result
        return result

    mono_types, fsms, errors, player_components = collections.Counter(), [], [], []
    for obj in objects:
        if obj.type.name == 'CharacterController':
            data = obj.read_typetree()
            player_components.append({'type': 'CharacterController', 'path': object_path(data['m_GameObject']['m_PathID']), 'serializedId': obj.path_id})
        if obj.type.name != 'MonoBehaviour':
            continue
        head = obj.parse_monobehaviour_head()
        script = head.m_Script.deref_parse_as_object()
        class_name = script.m_ClassName
        mono_types[class_name] += 1
        path = object_path(head.m_GameObject.m_PathID)
        if class_name in ('CharacterMotor', 'FPSInputController', 'MouseLook', 'ES2UniqueID', 'UniqueSaveManager', 'UniqueObjectManager', 'ES2GlobalSettings', 'CarSetup'):
            entry = {'type': class_name, 'path': path, 'serializedId': obj.path_id}
            player_components.append(entry)
            try:
                entry['data'] = obj.read_typetree(nodes=legacy_node(obj.generate_monobehaviour_node()))
            except Exception as exc:
                entry['parseError'] = str(exc)
        if class_name != 'PlayMakerFSM':
            continue
        try:
            tree = obj.read_typetree(nodes=legacy_node(obj.generate_monobehaviour_node()))
            fsm = tree['fsm']
            states = []
            for state in fsm['states']:
                data = state['actionData']
                states.append({'name': state['name'], 'transitions': state['transitions'], 'actions': [action_summary(data, i, a) for i, a in enumerate(data['actionNames'])]})
            entry = {'path': path, 'serializedId': obj.path_id, 'gameObjectId': head.m_GameObject.m_PathID, 'enabled': bool(head.m_Enabled), 'name': fsm['name'], 'startState': fsm['startState'], 'states': states, 'variables': fsm['variables']}
            fsms.append(entry)
        except Exception as exc:
            errors.append({'id': obj.path_id, 'path': path, 'type': class_name, 'error': str(exc)})
    result = {'scene': args.scene, 'sha256': hashlib.sha256(scene.read_bytes()).hexdigest(), 'unityPyVersion': UnityPy.__version__, 'objectCounts': collections.Counter(o.type.name for o in objects), 'monoTypes': mono_types, 'playerAndPersistenceComponents': player_components, 'fsms': fsms, 'errors': errors}
    output = args.output or ROOT / '.local' / 'inspection' / ('scene-' + args.scene + '.json')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps({'output': str(output), 'parsedFsms': len(fsms), 'errors': len(errors), 'components': [{k: v for k, v in x.items() if k != 'data'} for x in player_components], 'monoTypes': mono_types}, indent=2))


if __name__ == '__main__':
    main()
