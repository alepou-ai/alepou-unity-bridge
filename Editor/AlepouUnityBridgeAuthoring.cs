using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Alepou.UnityBridge
{
    internal static class AlepouUnityBridgeAuthoring
    {
        private const int MaxBatchEdits = 100;
        private const int MaxAnimationCurves = 100;
        private const int MaxAnimationKeysPerCurve = 200;
        private const int MaxSerializedArrayValues = 500;

        private static readonly HashSet<string> SupportedActions = new HashSet<string>(
            new[]
            {
                "create_prefab",
                "instantiate_prefab",
                "edit_prefab",
                "create_material",
                "set_material_property",
                "create_shader_asset",
                "create_animation_clip",
                "create_light",
                "bake_lighting",
                "clear_baked_lighting",
                "batch_edit_objects",
                "execute_menu_item"
            },
            StringComparer.Ordinal);

        private static readonly HashSet<string> AllowedMenuItems = new HashSet<string>(
            new[]
            {
                "Assets/Refresh",
                "File/Save",
                "File/Save Project"
            },
            StringComparer.Ordinal);

        internal static bool Supports(string actionName)
        {
            return SupportedActions.Contains((actionName ?? "").Trim().ToLowerInvariant());
        }

        internal static string Apply(AuthoringRequest request, List<string> changed)
        {
            if (request == null) throw new InvalidOperationException("Authoring request is empty.");
            var action = (request.action ?? "").Trim().ToLowerInvariant();
            switch (action)
            {
                case "create_prefab":
                    return CreatePrefab(request, changed);
                case "instantiate_prefab":
                    return InstantiatePrefab(request, changed);
                case "edit_prefab":
                    return EditPrefab(request, changed);
                case "create_material":
                    return CreateMaterial(request, changed);
                case "set_material_property":
                    return SetMaterialProperty(request, changed);
                case "create_shader_asset":
                    return CreateShaderAsset(request, changed);
                case "create_animation_clip":
                    return CreateAnimationClip(request, changed);
                case "create_light":
                    return CreateLight(request, changed);
                case "bake_lighting":
                    return BakeLighting(changed);
                case "clear_baked_lighting":
                    return ClearBakedLighting(changed);
                case "batch_edit_objects":
                    return BatchEditObjects(request, changed);
                case "execute_menu_item":
                    return ExecuteMenuItem(request, changed);
                default:
                    throw new InvalidOperationException("Unsupported authoring action: " + action);
            }
        }

        private static string CreatePrefab(AuthoringRequest request, List<string> changed)
        {
            var source = RequireSceneObject(request.objectPath, "create_prefab source");
            var assetPath = RequireNewAssetPath(request.assetPath, ".prefab");
            EnsureAssetFolder(Path.GetDirectoryName(assetPath).Replace("\\", "/"));
            bool success;
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, assetPath, out success);
            if (!success || prefab == null) throw new InvalidOperationException("Unity failed to create prefab: " + assetPath);
            AssetDatabase.SaveAssets();
            changed.Add(assetPath);
            return "create_prefab " + assetPath + " from " + ObjectPath(source);
        }

        private static string InstantiatePrefab(AuthoringRequest request, List<string> changed)
        {
            var assetPath = RequireExistingAssetPath(request.assetPath, ".prefab");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null) throw new InvalidOperationException("Prefab asset not found: " + assetPath);
            var instance = PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene()) as GameObject;
            if (instance == null) throw new InvalidOperationException("Unity failed to instantiate prefab: " + assetPath);
            Undo.RegisterCreatedObjectUndo(instance, "Alepou instantiate prefab");
            if (!string.IsNullOrWhiteSpace(request.parent))
            {
                var parent = RequireSceneObject(request.parent, "instantiate_prefab parent");
                Undo.SetTransformParent(instance.transform, parent.transform, "Alepou parent prefab instance");
            }
            if (!string.IsNullOrWhiteSpace(request.newName))
            {
                Undo.RecordObject(instance, "Alepou rename prefab instance");
                instance.name = SafeObjectName(request.newName);
            }
            ApplyTransform(
                instance.transform,
                request.position,
                request.setPosition,
                request.rotationEuler,
                request.setRotationEuler,
                request.localPosition,
                request.setLocalPosition,
                request.localRotationEuler,
                request.setLocalRotationEuler,
                request.localScale,
                request.setLocalScale);
            EditorSceneManager.MarkSceneDirty(instance.scene);
            var objectPath = ObjectPath(instance);
            changed.Add(objectPath);
            return "instantiate_prefab " + assetPath + " as " + objectPath;
        }

        private static string EditPrefab(AuthoringRequest request, List<string> changed)
        {
            var assetPath = RequireExistingAssetPath(request.assetPath, ".prefab");
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null) throw new InvalidOperationException("Unity could not load prefab contents: " + assetPath);
            try
            {
                var target = FindPrefabChild(root, request.childPath);
                var changedAnything = ApplyTransform(
                    target.transform,
                    request.position,
                    request.setPosition,
                    request.rotationEuler,
                    request.setRotationEuler,
                    request.localPosition,
                    request.setLocalPosition,
                    request.localRotationEuler,
                    request.setLocalRotationEuler,
                    request.localScale,
                    request.setLocalScale);
                if (!string.IsNullOrWhiteSpace(request.component) || !string.IsNullOrWhiteSpace(request.field))
                {
                    if (string.IsNullOrWhiteSpace(request.component) || string.IsNullOrWhiteSpace(request.field))
                    {
                        throw new InvalidOperationException("edit_prefab component and field must be supplied together.");
                    }
                    var component = RequireComponent(target, request.component);
                    SetSerializedProperty(component, request.field, request.value, request.values);
                    changedAnything = true;
                }
                if (!changedAnything)
                {
                    throw new InvalidOperationException("edit_prefab requires a transform or component field edit.");
                }
                bool success;
                PrefabUtility.SaveAsPrefabAsset(root, assetPath, out success);
                if (!success) throw new InvalidOperationException("Unity failed to save prefab edits: " + assetPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            AssetDatabase.SaveAssets();
            changed.Add(assetPath);
            return "edit_prefab " + assetPath + (string.IsNullOrWhiteSpace(request.childPath) ? "" : " child " + request.childPath);
        }

        private static string CreateMaterial(AuthoringRequest request, List<string> changed)
        {
            var assetPath = RequireNewAssetPath(request.assetPath, ".mat");
            var shader = ResolveShader(request.shader, request.shaderAssetPath);
            if (shader == null) throw new InvalidOperationException("Shader not found for create_material.");
            EnsureAssetFolder(Path.GetDirectoryName(assetPath).Replace("\\", "/"));
            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(assetPath) };
            if (ShouldApplyColor(request.color, request.setColor))
            {
                var property = ResolveMaterialProperty(material, request.propertyName, true);
                material.SetColor(property, request.color.ToColor());
            }
            AssetDatabase.CreateAsset(material, assetPath);
            AssetDatabase.SaveAssets();
            changed.Add(assetPath);
            return "create_material " + assetPath + " shader " + shader.name;
        }

        private static string SetMaterialProperty(AuthoringRequest request, List<string> changed)
        {
            var assetPath = RequireExistingAssetPath(request.assetPath, ".mat");
            var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (material == null) throw new InvalidOperationException("Material not found: " + assetPath);
            var property = (request.propertyName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(property)) throw new InvalidOperationException("set_material_property requires propertyName.");
            if (!material.HasProperty(property)) throw new InvalidOperationException("Material does not expose property: " + property);
            var valueType = (request.valueType ?? "").Trim().ToLowerInvariant();
            Undo.RecordObject(material, "Alepou set material property");
            switch (valueType)
            {
                case "float":
                    material.SetFloat(property, ParseFloat(request.value, "material float"));
                    break;
                case "int":
                    material.SetInt(property, ParseInt(request.value, "material int"));
                    break;
                case "color":
                    if (request.color == null) throw new InvalidOperationException("Material color requires color.");
                    material.SetColor(property, request.color.ToColor());
                    break;
                case "vector":
                    if (request.vector == null) throw new InvalidOperationException("Material vector requires vector.");
                    material.SetVector(property, request.vector.ToVector4());
                    break;
                case "texture":
                    var texturePath = RequireExistingAssetPath(request.textureAssetPath, null);
                    var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                    if (texture == null) throw new InvalidOperationException("Texture asset not found: " + texturePath);
                    material.SetTexture(property, texture);
                    changed.Add(texturePath);
                    break;
                default:
                    throw new InvalidOperationException("set_material_property valueType must be float, int, color, vector, or texture.");
            }
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            changed.Add(assetPath);
            return "set_material_property " + property + " on " + assetPath;
        }

        private static string CreateShaderAsset(AuthoringRequest request, List<string> changed)
        {
            var assetPath = RequireNewAssetPath(request.assetPath, ".shader");
            var template = (request.template ?? "").Trim().ToLowerInvariant();
            var shaderName = string.IsNullOrWhiteSpace(request.shaderName)
                ? "Alepou/Generated/" + Path.GetFileNameWithoutExtension(assetPath)
                : SafeShaderName(request.shaderName);
            var source = ShaderTemplate(template, shaderName);
            EnsureAssetFolder(Path.GetDirectoryName(assetPath).Replace("\\", "/"));
            var absolute = ProjectAbsolutePath(assetPath);
            File.WriteAllText(absolute, source, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
            if (shader == null) throw new InvalidOperationException("Generated shader did not import successfully: " + assetPath);
            changed.Add(assetPath);
            return "create_shader_asset " + assetPath + " template " + template;
        }

        private static string CreateAnimationClip(AuthoringRequest request, List<string> changed)
        {
            var assetPath = RequireNewAssetPath(request.assetPath, ".anim");
            var curves = request.curves ?? Array.Empty<CurveRequest>();
            if (curves.Length == 0) throw new InvalidOperationException("create_animation_clip requires at least one curve.");
            if (curves.Length > MaxAnimationCurves)
            {
                throw new InvalidOperationException("create_animation_clip exceeds the " + MaxAnimationCurves + "-curve limit.");
            }
            var clip = new AnimationClip
            {
                name = Path.GetFileNameWithoutExtension(assetPath),
                frameRate = request.frameRate > 0 ? Math.Min(request.frameRate, 240f) : 60f
            };
            foreach (var curve in curves)
            {
                if (curve == null || string.IsNullOrWhiteSpace(curve.componentType) || string.IsNullOrWhiteSpace(curve.propertyName))
                {
                    throw new InvalidOperationException("Every animation curve requires componentType and propertyName.");
                }
                var keys = curve.keys ?? Array.Empty<AnimationKeyRequest>();
                if (keys.Length == 0 || keys.Length > MaxAnimationKeysPerCurve)
                {
                    throw new InvalidOperationException(
                        "Animation curve key count must be 1-" + MaxAnimationKeysPerCurve + ".");
                }
                var componentType = ResolveComponentType(curve.componentType);
                var animationCurve = new AnimationCurve(keys.Select(key =>
                    new Keyframe(key.time, key.value, key.inTangent, key.outTangent)).ToArray());
                clip.SetCurve(
                    string.IsNullOrWhiteSpace(curve.relativePath) ? "" : curve.relativePath.Trim(),
                    componentType,
                    curve.propertyName.Trim(),
                    animationCurve);
            }
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = request.loopTime;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            EnsureAssetFolder(Path.GetDirectoryName(assetPath).Replace("\\", "/"));
            AssetDatabase.CreateAsset(clip, assetPath);
            AssetDatabase.SaveAssets();
            changed.Add(assetPath);
            return "create_animation_clip " + assetPath + " with " + curves.Length + " curve(s)";
        }

        private static string CreateLight(AuthoringRequest request, List<string> changed)
        {
            if (string.IsNullOrWhiteSpace(request.objectPath)) throw new InvalidOperationException("create_light requires objectPath.");
            if (FindSceneObject(request.objectPath) != null) throw new InvalidOperationException("create_light target already exists: " + request.objectPath);
            var gameObject = CreateSceneObjectPath(request.objectPath, changed);
            var light = Undo.AddComponent<Light>(gameObject);
            var type = LightType.Point;
            if (!string.IsNullOrWhiteSpace(request.lightType) &&
                !Enum.TryParse(request.lightType.Trim(), true, out type))
            {
                throw new InvalidOperationException("Unknown LightType: " + request.lightType);
            }
            light.type = type;
            light.color = ShouldApplyColor(request.color, request.setColor) ? request.color.ToColor() : Color.white;
            if (request.setIntensity && request.intensity < 0)
            {
                throw new InvalidOperationException("Light intensity cannot be negative.");
            }
            light.intensity = request.setIntensity || request.intensity > 0 ? request.intensity : 1f;
            light.range = request.range > 0 ? request.range : 10f;
            if (light.type == LightType.Spot) light.spotAngle = request.spotAngle > 0 ? request.spotAngle : 30f;
            if (!string.IsNullOrWhiteSpace(request.lightShadows))
            {
                LightShadows shadows;
                if (!Enum.TryParse(request.lightShadows.Trim(), true, out shadows))
                {
                    throw new InvalidOperationException("Unknown LightShadows value: " + request.lightShadows);
                }
                light.shadows = shadows;
            }
            ApplyTransform(
                gameObject.transform,
                request.position,
                request.setPosition,
                request.rotationEuler,
                request.setRotationEuler,
                request.localPosition,
                request.setLocalPosition,
                request.localRotationEuler,
                request.setLocalRotationEuler,
                request.localScale,
                request.setLocalScale);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            var path = ObjectPath(gameObject);
            changed.Add(path);
            return "create_light " + path + " type " + light.type;
        }

        private static string BakeLighting(List<string> changed)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Unity must be import/compile idle before baking lighting.");
            }
            if (!Lightmapping.Bake()) throw new InvalidOperationException("Unity lighting bake did not complete successfully.");
            changed.Add("LightingData");
            return "bake_lighting";
        }

        private static string ClearBakedLighting(List<string> changed)
        {
            Lightmapping.Clear();
            changed.Add("LightingData");
            return "clear_baked_lighting";
        }

        private static string BatchEditObjects(AuthoringRequest request, List<string> changed)
        {
            var edits = request.objectEdits ?? Array.Empty<ObjectEditRequest>();
            if (edits.Length == 0) throw new InvalidOperationException("batch_edit_objects requires objectEdits.");
            if (edits.Length > MaxBatchEdits)
            {
                throw new InvalidOperationException("batch_edit_objects exceeds the " + MaxBatchEdits + "-object limit.");
            }
            foreach (var edit in edits)
            {
                if (edit == null || string.IsNullOrWhiteSpace(edit.objectPath))
                {
                    throw new InvalidOperationException("Every batch object edit requires objectPath.");
                }
                var gameObject = RequireSceneObject(edit.objectPath, "batch_edit_objects target");
                var changedAnything = ApplyTransform(
                    gameObject.transform,
                    edit.position,
                    edit.setPosition,
                    edit.rotationEuler,
                    edit.setRotationEuler,
                    edit.localPosition,
                    edit.setLocalPosition,
                    edit.localRotationEuler,
                    edit.setLocalRotationEuler,
                    edit.localScale,
                    edit.setLocalScale);
                if (!string.IsNullOrWhiteSpace(edit.component) || !string.IsNullOrWhiteSpace(edit.field))
                {
                    if (string.IsNullOrWhiteSpace(edit.component) || string.IsNullOrWhiteSpace(edit.field))
                    {
                        throw new InvalidOperationException("Batch edit component and field must be supplied together.");
                    }
                    SetSerializedProperty(
                        RequireComponent(gameObject, edit.component),
                        edit.field,
                        edit.value,
                        edit.values);
                    changedAnything = true;
                }
                if (!changedAnything) throw new InvalidOperationException("Batch edit has no operation for " + edit.objectPath);
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
                changed.Add(ObjectPath(gameObject));
            }
            return "batch_edit_objects " + edits.Length + " object(s)";
        }

        private static string ExecuteMenuItem(AuthoringRequest request, List<string> changed)
        {
            var menuPath = (request.menuPath ?? request.path ?? "").Trim();
            if (!AllowedMenuItems.Contains(menuPath))
            {
                throw new InvalidOperationException(
                    "Editor menu item is not allowlisted: " + menuPath +
                    ". Allowed: " + string.Join(", ", AllowedMenuItems.OrderBy(value => value).ToArray()));
            }
            if (!EditorApplication.ExecuteMenuItem(menuPath))
            {
                throw new InvalidOperationException("Unity did not execute allowlisted menu item: " + menuPath);
            }
            changed.Add(menuPath == "Assets/Refresh" ? "Assets" : "Editor");
            return "execute_menu_item " + menuPath;
        }

        private static Shader ResolveShader(string shaderName, string shaderAssetPath)
        {
            if (!string.IsNullOrWhiteSpace(shaderAssetPath))
            {
                var path = RequireExistingAssetPath(shaderAssetPath, ".shader");
                return AssetDatabase.LoadAssetAtPath<Shader>(path);
            }
            if (string.IsNullOrWhiteSpace(shaderName)) shaderName = "Standard";
            return Shader.Find(shaderName.Trim());
        }

        private static string ResolveMaterialProperty(Material material, string requested, bool color)
        {
            if (!string.IsNullOrWhiteSpace(requested))
            {
                if (!material.HasProperty(requested.Trim()))
                {
                    throw new InvalidOperationException("Material does not expose property: " + requested.Trim());
                }
                return requested.Trim();
            }
            foreach (var candidate in color
                         ? new[] { "_BaseColor", "_Color" }
                         : Array.Empty<string>())
            {
                if (material.HasProperty(candidate)) return candidate;
            }
            throw new InvalidOperationException("Material shader has no recognized default color property; provide propertyName.");
        }

        private static string ShaderTemplate(string template, string shaderName)
        {
            if (template == "unlit-color")
            {
                return "Shader \"" + shaderName + "\"\n" +
                       "{\n" +
                       "    Properties { _BaseColor (\"Color\", Color) = (1,1,1,1) }\n" +
                       "    SubShader\n" +
                       "    {\n" +
                       "        Tags { \"RenderType\"=\"Opaque\" }\n" +
                       "        Pass\n" +
                       "        {\n" +
                       "            CGPROGRAM\n" +
                       "            #pragma vertex vert\n" +
                       "            #pragma fragment frag\n" +
                       "            #include \"UnityCG.cginc\"\n" +
                       "            fixed4 _BaseColor;\n" +
                       "            struct appdata { float4 vertex : POSITION; };\n" +
                       "            struct v2f { float4 vertex : SV_POSITION; };\n" +
                       "            v2f vert(appdata v) { v2f o; o.vertex = UnityObjectToClipPos(v.vertex); return o; }\n" +
                       "            fixed4 frag(v2f i) : SV_Target { return _BaseColor; }\n" +
                       "            ENDCG\n" +
                       "        }\n" +
                       "    }\n" +
                       "}\n";
            }
            if (template == "unlit-texture")
            {
                return "Shader \"" + shaderName + "\"\n" +
                       "{\n" +
                       "    Properties { _MainTex (\"Texture\", 2D) = \"white\" {} _BaseColor (\"Tint\", Color) = (1,1,1,1) }\n" +
                       "    SubShader\n" +
                       "    {\n" +
                       "        Tags { \"RenderType\"=\"Opaque\" }\n" +
                       "        Pass\n" +
                       "        {\n" +
                       "            CGPROGRAM\n" +
                       "            #pragma vertex vert\n" +
                       "            #pragma fragment frag\n" +
                       "            #include \"UnityCG.cginc\"\n" +
                       "            sampler2D _MainTex; float4 _MainTex_ST; fixed4 _BaseColor;\n" +
                       "            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };\n" +
                       "            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };\n" +
                       "            v2f vert(appdata v) { v2f o; o.vertex = UnityObjectToClipPos(v.vertex); o.uv = TRANSFORM_TEX(v.uv, _MainTex); return o; }\n" +
                       "            fixed4 frag(v2f i) : SV_Target { return tex2D(_MainTex, i.uv) * _BaseColor; }\n" +
                       "            ENDCG\n" +
                       "        }\n" +
                       "    }\n" +
                       "}\n";
            }
            throw new InvalidOperationException("create_shader_asset template must be unlit-color or unlit-texture.");
        }

        private static string SafeShaderName(string value)
        {
            var name = value.Trim();
            if (name.Length == 0 || name.Length > 200 ||
                name.IndexOfAny(new[] { '"', '\\', '\r', '\n', '\0' }) >= 0)
            {
                throw new InvalidOperationException("shaderName is invalid.");
            }
            return name;
        }

        private static bool ApplyTransform(
            Transform transform,
            Vector3Request position,
            bool setPosition,
            Vector3Request rotationEuler,
            bool setRotationEuler,
            Vector3Request localPosition,
            bool setLocalPosition,
            Vector3Request localRotationEuler,
            bool setLocalRotationEuler,
            Vector3Request localScale,
            bool setLocalScale)
        {
            var applyPosition = ShouldApplyVector(position, setPosition);
            var applyRotation = ShouldApplyVector(rotationEuler, setRotationEuler);
            var applyLocalPosition = ShouldApplyVector(localPosition, setLocalPosition);
            var applyLocalRotation = ShouldApplyVector(localRotationEuler, setLocalRotationEuler);
            var applyLocalScale = ShouldApplyVector(localScale, setLocalScale);
            if (!applyPosition && !applyRotation && !applyLocalPosition &&
                !applyLocalRotation && !applyLocalScale)
            {
                return false;
            }
            Undo.RecordObject(transform, "Alepou authoring transform");
            if (applyPosition) transform.position = position.ToVector3();
            if (applyRotation) transform.rotation = Quaternion.Euler(rotationEuler.ToVector3());
            if (applyLocalPosition) transform.localPosition = localPosition.ToVector3();
            if (applyLocalRotation) transform.localRotation = Quaternion.Euler(localRotationEuler.ToVector3());
            if (applyLocalScale) transform.localScale = localScale.ToVector3();
            return true;
        }

        private static bool ShouldApplyVector(Vector3Request value, bool explicitSet)
        {
            if (explicitSet && value == null)
            {
                throw new InvalidOperationException("An explicit vector presence flag requires its vector value.");
            }
            return explicitSet ||
                   value != null &&
                   (Mathf.Abs(value.x) > Mathf.Epsilon ||
                    Mathf.Abs(value.y) > Mathf.Epsilon ||
                    Mathf.Abs(value.z) > Mathf.Epsilon);
        }

        private static bool ShouldApplyColor(ColorRequest value, bool explicitSet)
        {
            if (explicitSet && value == null)
            {
                throw new InvalidOperationException("setColor requires a color value.");
            }
            return explicitSet ||
                   value != null &&
                   (Mathf.Abs(value.r) > Mathf.Epsilon ||
                    Mathf.Abs(value.g) > Mathf.Epsilon ||
                    Mathf.Abs(value.b) > Mathf.Epsilon);
        }

        private static void SetSerializedProperty(UnityEngine.Object target, string field, string value, string[] values)
        {
            if (target == null) throw new InvalidOperationException("Serialized property target is missing.");
            if (string.IsNullOrWhiteSpace(field)) throw new InvalidOperationException("Serialized property field is required.");
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(field.Trim());
            if (property == null) throw new InvalidOperationException("Serialized property not found: " + field);
            Undo.RecordObject(target, "Alepou authoring property");
            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                var items = values ?? Array.Empty<string>();
                if (items.Length > MaxSerializedArrayValues)
                {
                    throw new InvalidOperationException("Serialized array exceeds the " + MaxSerializedArrayValues + "-value limit.");
                }
                property.arraySize = items.Length;
                for (var i = 0; i < items.Length; i++)
                {
                    SetScalarProperty(property.GetArrayElementAtIndex(i), items[i]);
                }
            }
            else
            {
                SetScalarProperty(property, value);
            }
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
        }

        private static void SetScalarProperty(SerializedProperty property, string value)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    property.intValue = ParseInt(value, property.propertyPath);
                    break;
                case SerializedPropertyType.Boolean:
                    bool boolValue;
                    if (!bool.TryParse(value, out boolValue)) throw new InvalidOperationException("Invalid bool for " + property.propertyPath);
                    property.boolValue = boolValue;
                    break;
                case SerializedPropertyType.Float:
                    property.floatValue = ParseFloat(value, property.propertyPath);
                    break;
                case SerializedPropertyType.String:
                    property.stringValue = value ?? "";
                    break;
                case SerializedPropertyType.Enum:
                    int enumIndex;
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out enumIndex))
                    {
                        if (enumIndex < 0 || enumIndex >= property.enumDisplayNames.Length)
                        {
                            throw new InvalidOperationException("Enum index out of range for " + property.propertyPath);
                        }
                        property.enumValueIndex = enumIndex;
                    }
                    else
                    {
                        var match = Array.FindIndex(
                            property.enumDisplayNames,
                            name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase));
                        if (match < 0) throw new InvalidOperationException("Unknown enum value for " + property.propertyPath + ": " + value);
                        property.enumValueIndex = match;
                    }
                    break;
                default:
                    throw new InvalidOperationException(
                        "Unsupported serialized property type for authoring edit: " + property.propertyType);
            }
        }

        private static Type ResolveComponentType(string name)
        {
            var type = ResolveType(name);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                throw new InvalidOperationException("Component type not found: " + name);
            }
            return type;
        }

        private static Type ResolveType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var requested = name.Trim();
            var direct = Type.GetType(requested, false);
            if (direct != null) return direct;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var full = assembly.GetType(requested, false);
                    if (full != null) return full;
                    var shortMatch = assembly.GetTypes().FirstOrDefault(type =>
                        string.Equals(type.Name, requested, StringComparison.Ordinal));
                    if (shortMatch != null) return shortMatch;
                }
                catch (ReflectionTypeLoadException ex)
                {
                    var match = ex.Types.FirstOrDefault(type =>
                        type != null &&
                        (string.Equals(type.FullName, requested, StringComparison.Ordinal) ||
                         string.Equals(type.Name, requested, StringComparison.Ordinal)));
                    if (match != null) return match;
                }
                catch { }
            }
            return null;
        }

        private static Component RequireComponent(GameObject gameObject, string componentName)
        {
            var type = ResolveComponentType(componentName);
            var component = gameObject.GetComponent(type);
            if (component == null)
            {
                throw new InvalidOperationException(
                    "Component " + componentName + " not found on " + ObjectPath(gameObject));
            }
            return component;
        }

        private static GameObject FindPrefabChild(GameObject root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return root;
            var normalized = relativePath.Trim().Trim('/');
            if (string.Equals(normalized, root.name, StringComparison.Ordinal)) return root;
            if (normalized.StartsWith(root.name + "/", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(root.name.Length + 1);
            }
            var target = root.transform.Find(normalized);
            if (target == null) throw new InvalidOperationException("Prefab child not found: " + relativePath);
            return target.gameObject;
        }

        private static GameObject RequireSceneObject(string path, string label)
        {
            var gameObject = FindSceneObject(path);
            if (gameObject == null) throw new InvalidOperationException(label + " not found: " + path);
            return gameObject;
        }

        private static GameObject FindSceneObject(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var parts = path.Trim().Trim('/').Split('/');
            foreach (var scene in Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt))
            {
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (!string.Equals(root.name, parts[0], StringComparison.Ordinal)) continue;
                    var current = root.transform;
                    var matched = true;
                    for (var i = 1; i < parts.Length; i++)
                    {
                        current = current.Find(parts[i]);
                        if (current != null) continue;
                        matched = false;
                        break;
                    }
                    if (matched && current != null) return current.gameObject;
                }
            }
            return null;
        }

        private static GameObject CreateSceneObjectPath(string path, List<string> changed)
        {
            var parts = path.Trim().Trim('/').Split('/');
            if (parts.Length == 0 || parts.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException("Invalid scene object path: " + path);
            }
            GameObject current = null;
            var currentPath = "";
            foreach (var rawPart in parts)
            {
                var part = SafeObjectName(rawPart);
                currentPath = string.IsNullOrWhiteSpace(currentPath) ? part : currentPath + "/" + part;
                var existing = FindSceneObject(currentPath);
                if (existing != null)
                {
                    current = existing;
                    continue;
                }
                var created = new GameObject(part);
                Undo.RegisterCreatedObjectUndo(created, "Alepou create authoring object");
                if (current != null) Undo.SetTransformParent(created.transform, current.transform, "Alepou parent authoring object");
                current = created;
                changed.Add(currentPath);
            }
            return current;
        }

        private static string SafeObjectName(string raw)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0 || value.Length > 200 || value.IndexOfAny(new[] { '/', '\\', '\r', '\n', '\0' }) >= 0)
            {
                throw new InvalidOperationException("Invalid object name: " + raw);
            }
            return value;
        }

        private static string ObjectPath(GameObject gameObject)
        {
            var names = new List<string>();
            var current = gameObject == null ? null : gameObject.transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static string RequireNewAssetPath(string raw, string extension)
        {
            var path = NormalizeAssetPath(raw, extension);
            if (AssetDatabase.LoadMainAssetAtPath(path) != null || File.Exists(ProjectAbsolutePath(path)))
            {
                throw new InvalidOperationException("Asset already exists; authoring actions never overwrite: " + path);
            }
            return path;
        }

        private static string RequireExistingAssetPath(string raw, string extension)
        {
            var path = NormalizeAssetPath(raw, extension);
            if (AssetDatabase.LoadMainAssetAtPath(path) == null && !File.Exists(ProjectAbsolutePath(path)))
            {
                throw new InvalidOperationException("Asset not found: " + path);
            }
            return path;
        }

        private static string NormalizeAssetPath(string raw, string extension)
        {
            var path = (raw ?? "").Trim().Replace("\\", "/");
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) ||
                path.Contains("..") ||
                path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                throw new InvalidOperationException("Asset path must remain under Assets/: " + raw);
            }
            if (!string.IsNullOrWhiteSpace(extension) &&
                !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Asset path must end with " + extension + ": " + path);
            }
            ProjectAbsolutePath(path);
            return path;
        }

        private static string ProjectAbsolutePath(string assetPath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var root = Path.GetFullPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Asset path escapes the project: " + assetPath);
            }
            return full;
        }

        private static void EnsureAssetFolder(string folder)
        {
            var normalized = (folder ?? "").Replace("\\", "/").TrimEnd('/');
            if (string.IsNullOrWhiteSpace(normalized) || normalized == "Assets") return;
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) || normalized.Contains(".."))
            {
                throw new InvalidOperationException("Invalid Assets folder: " + folder);
            }
            var current = "Assets";
            foreach (var part in normalized.Substring("Assets/".Length).Split('/'))
            {
                var next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next))
                {
                    var guid = AssetDatabase.CreateFolder(current, part);
                    if (string.IsNullOrWhiteSpace(guid)) throw new InvalidOperationException("Could not create asset folder: " + next);
                }
                current = next;
            }
        }

        private static int ParseInt(string raw, string label)
        {
            int value;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                throw new InvalidOperationException("Invalid integer for " + label + ": " + raw);
            }
            return value;
        }

        private static float ParseFloat(string raw, string label)
        {
            float value;
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                float.IsNaN(value) ||
                float.IsInfinity(value))
            {
                throw new InvalidOperationException("Invalid float for " + label + ": " + raw);
            }
            return value;
        }

        [Serializable]
        internal sealed class AuthoringRequest
        {
            public string action;
            public string path;
            public string objectPath;
            public string parent;
            public string newName;
            public string assetPath;
            public string childPath;
            public string component;
            public string field;
            public string value;
            public string[] values;
            public string shader;
            public string shaderAssetPath;
            public string shaderName;
            public string template;
            public string propertyName;
            public string valueType;
            public string textureAssetPath;
            public string menuPath;
            public string lightType;
            public string lightShadows;
            public float intensity;
            public float range;
            public float spotAngle;
            public float frameRate;
            public bool loopTime;
            public bool setColor;
            public bool setIntensity;
            public bool setPosition;
            public bool setRotationEuler;
            public bool setLocalPosition;
            public bool setLocalRotationEuler;
            public bool setLocalScale;
            public ColorRequest color;
            public Vector4Request vector;
            public Vector3Request position;
            public Vector3Request rotationEuler;
            public Vector3Request localPosition;
            public Vector3Request localRotationEuler;
            public Vector3Request localScale;
            public CurveRequest[] curves;
            public ObjectEditRequest[] objectEdits;
        }

        [Serializable]
        internal sealed class Vector3Request
        {
            public float x;
            public float y;
            public float z;
            public Vector3 ToVector3() { return new Vector3(x, y, z); }
        }

        [Serializable]
        internal sealed class Vector4Request
        {
            public float x;
            public float y;
            public float z;
            public float w;
            public Vector4 ToVector4() { return new Vector4(x, y, z, w); }
        }

        [Serializable]
        internal sealed class ColorRequest
        {
            public float r;
            public float g;
            public float b;
            public float a = 1f;
            public Color ToColor() { return new Color(r, g, b, a); }
        }

        [Serializable]
        internal sealed class CurveRequest
        {
            public string relativePath;
            public string componentType;
            public string propertyName;
            public AnimationKeyRequest[] keys;
        }

        [Serializable]
        internal sealed class AnimationKeyRequest
        {
            public float time;
            public float value;
            public float inTangent;
            public float outTangent;
        }

        [Serializable]
        internal sealed class ObjectEditRequest
        {
            public string objectPath;
            public string component;
            public string field;
            public string value;
            public string[] values;
            public bool setPosition;
            public bool setRotationEuler;
            public bool setLocalPosition;
            public bool setLocalRotationEuler;
            public bool setLocalScale;
            public Vector3Request position;
            public Vector3Request rotationEuler;
            public Vector3Request localPosition;
            public Vector3Request localRotationEuler;
            public Vector3Request localScale;
        }
    }
}
