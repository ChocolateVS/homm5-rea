# Applies this workstream's AdvWorld.cs hunks (original-lighting hook) to a given copy of the file.
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8').read()
if 'Render.H5Lighting.Apply' not in t:
    t = t.replace("""            if (_env[f] != null) _env[f]!.Environment = f == floor ? _envRes[f] : null;
        }""", """            if (_env[f] != null) _env[f]!.Environment = f == floor ? _envRes[f] : null;
        }
        // Original-renderer lighting and fog for the visible floor (H5Lit materials/terrain).
        Render.H5Lighting.Apply(_lightRec[floor], _lightFile[floor], p => _session.Files.Exists(p) ? _session.Files.LoadXml(p).Root : null);""", 1)
    t = t.replace("""    readonly Godot.Environment?[] _envRes = new Godot.Environment?[2];""", """    readonly Godot.Environment?[] _envRes = new Godot.Environment?[2];
    readonly XElement?[] _lightRec = new XElement?[2];
    readonly string?[] _lightFile = new string?[2];""", 1)
    t = t.replace("""        Color C(Vector3 v, float scale = 1) =>""", """        _lightRec[f] = light;
        _lightFile[f] = lightFile;
        Color C(Vector3 v, float scale = 1) =>""", 1)
    open(p, 'w', encoding='utf-8').write(t)
print('ok' if 'Render.H5Lighting.Apply' in t else 'FAILED')
