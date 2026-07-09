// Impostor Billboard Mesh Generator
// Amplify Impostors 의 GenerateMesh(팔각형 ShapePoints + 삼각분할)를 재현한 경량 유틸리티.
// 평면 Quad 대신 실루엣에 가까운 볼록 폴리곤을 써서 투명 픽셀(오버드로우)을 줄인다.
//
// 사용법: 상단 메뉴 Tools > Custom Impostor > Billboard Mesh Generator
//  - Shape: Quad / Octagon(Amplify 기본) / N-Gon 중 선택
//  - Impostor Size: 정점 범위(= 셰이더의 _ImpostorSize 에 같은 값을 넣을 것)
//  - Generate & Save: .asset 메시 생성. (선택된 오브젝트의 MeshFilter 에 바로 할당 가능)
//
// 참고: 임포스터 셰이더는 프레임 UV 를 정점 위치(positionOS.xy)에서 직접 계산하므로
//       메시의 UV 채널은 런타임에 쓰이지 않는다(디버그용으로만 채워둠).
//       메시는 중앙정렬(피벗 오프셋 없음)로 만들고, 실루엣 pixelOffset 보정은
//       셰이더의 _ReconstructPivot 토글이 처리한다.

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace CustomSphericalImpostor
{
    public class ImpostorMeshGenerator : EditorWindow
    {
        enum ShapeType { Quad, Octagon, NGon }

        ShapeType m_shape = ShapeType.Octagon;
        float m_size = 1f;          // 정점 범위 (셰이더 _ImpostorSize 와 일치시킬 값)
        float m_cornerCut = 0.15f;  // Octagon 모서리 컷 (Amplify 기본 0.15)
        int m_nGonSides = 8;        // N-Gon 변 수
        bool m_flipWinding = false; // Cull 방향이 반대로 나올 때 뒤집기
        string m_savePath = "Assets/CustomSphericalImpostor/ImpostorMesh.asset";

        // 피벗(_Offset) 자동 계산용
        GameObject m_sourceObject;      // 원본 오브젝트/프리팹 (바운드 중심 계산 대상)
        Material m_targetMaterial;      // _Offset 을 넣을 임포스터 머티리얼
        Vector3 m_lastComputedCenter;   // 마지막 계산 결과 표시용
        bool m_hasComputed;

        [MenuItem("Tools/Custom Impostor/Billboard Mesh Generator")]
        static void Open()
        {
            GetWindow<ImpostorMeshGenerator>("Impostor Mesh");
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Billboard Mesh 설정", EditorStyles.boldLabel);
            m_shape = (ShapeType)EditorGUILayout.EnumPopup("Shape", m_shape);
            m_size = EditorGUILayout.FloatField("Impostor Size (정점 범위)", m_size);

            if (m_shape == ShapeType.Octagon)
                m_cornerCut = EditorGUILayout.Slider("Corner Cut", m_cornerCut, 0f, 0.5f);
            if (m_shape == ShapeType.NGon)
                m_nGonSides = Mathf.Max(3, EditorGUILayout.IntField("Sides", m_nGonSides));

            m_flipWinding = EditorGUILayout.Toggle("Flip Winding (Cull 반대일 때)", m_flipWinding);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("저장", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            m_savePath = EditorGUILayout.TextField("Save Path", m_savePath);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string p = EditorUtility.SaveFilePanelInProject("Save Mesh", "ImpostorMesh", "asset", "메시 저장 위치");
                if (!string.IsNullOrEmpty(p)) m_savePath = p;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            if (GUILayout.Button("Generate & Save", GUILayout.Height(28)))
                GenerateAndSave(false);

            using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
            {
                if (GUILayout.Button("Generate & Assign to Selected MeshFilter", GUILayout.Height(24)))
                    GenerateAndSave(true);
            }

            EditorGUILayout.HelpBox(
                "생성 후 머티리얼의 Impostor Size 를 위 값과 동일하게 맞추세요.\n" +
                "메시는 중앙정렬입니다. 피벗/실루엣 보정은 셰이더의 _Offset 과 Reconstruct Pivot 토글이 담당합니다.",
                MessageType.Info);

            // ---- 피벗(_Offset) 자동 계산 헬퍼 ----
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Pivot (_Offset) 자동 계산", EditorStyles.boldLabel);
            m_sourceObject = (GameObject)EditorGUILayout.ObjectField("Source Object", m_sourceObject, typeof(GameObject), true);
            m_targetMaterial = (Material)EditorGUILayout.ObjectField("Target Material", m_targetMaterial, typeof(Material), false);

            using (new EditorGUI.DisabledScope(m_sourceObject == null))
            {
                if (GUILayout.Button("Compute Bounds Center → Set _Offset", GUILayout.Height(24)))
                    ComputeAndSetOffset();
            }

            if (m_hasComputed)
            {
                EditorGUILayout.LabelField("계산된 바운드 중심(로컬)", m_lastComputedCenter.ToString("F4"));
                if (GUILayout.Button("Copy to Clipboard"))
                    EditorGUIUtility.systemCopyBuffer =
                        $"{m_lastComputedCenter.x}, {m_lastComputedCenter.y}, {m_lastComputedCenter.z}";
            }

            EditorGUILayout.HelpBox(
                "Source Object 의 자식 메시들을 루트 로컬 공간에서 합산해 중심을 구합니다.\n" +
                "Target Material 을 지정하면 그 머티리얼의 _Offset.xyz 에 바로 세팅합니다.\n" +
                "시트는 바운드 중심 기준으로 렌더되지만 피벗은 다를 수 있으므로, 이 값이 그 차이를 메꿉니다.",
                MessageType.Info);
        }

        void ComputeAndSetOffset()
        {
            if (!TryComputeLocalBounds(m_sourceObject, out Bounds b))
            {
                Debug.LogWarning("[ImpostorMeshGenerator] Source Object 에서 메시를 찾지 못했습니다 (MeshFilter/SkinnedMeshRenderer 필요).");
                return;
            }

            m_lastComputedCenter = b.center;
            m_hasComputed = true;

            if (m_targetMaterial != null)
            {
                if (!m_targetMaterial.HasProperty("_Offset"))
                {
                    Debug.LogWarning("[ImpostorMeshGenerator] Target Material 에 _Offset 프로퍼티가 없습니다 (임포스터 셰이더인지 확인).");
                    return;
                }
                Undo.RecordObject(m_targetMaterial, "Set Impostor _Offset");
                Vector4 prev = m_targetMaterial.GetVector("_Offset");
                m_targetMaterial.SetVector("_Offset", new Vector4(b.center.x, b.center.y, b.center.z, prev.w));
                EditorUtility.SetDirty(m_targetMaterial);
                AssetDatabase.SaveAssets();
                Debug.Log($"[ImpostorMeshGenerator] '{m_targetMaterial.name}' _Offset = {b.center:F4} 로 세팅.");
            }
            else
            {
                Debug.Log($"[ImpostorMeshGenerator] 바운드 중심(로컬) = {b.center:F4} (Target Material 미지정 → 세팅은 안 함).");
            }
        }

        // 루트의 로컬 공간에서 자식 메시들의 합산 바운드를 구한다 (루트 자신의 트랜스폼은 제거).
        static bool TryComputeLocalBounds(GameObject root, out Bounds bounds)
        {
            bounds = new Bounds();
            Bounds accBounds = new Bounds(); // 로컬 함수가 out 파라미터를 못 잡으므로 로컬 변수 사용
            bool has = false;
            Matrix4x4 w2l = root.transform.worldToLocalMatrix;

            void Accumulate(Mesh mesh, Transform t)
            {
                if (mesh == null) return;
                Matrix4x4 m = w2l * t.localToWorldMatrix; // mesh 로컬 → 루트 로컬
                Bounds mb = mesh.bounds;
                Vector3 c = mb.center, e = mb.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = c + new Vector3(
                        (i & 1) == 0 ? -e.x : e.x,
                        (i & 2) == 0 ? -e.y : e.y,
                        (i & 4) == 0 ? -e.z : e.z);
                    Vector3 p = m.MultiplyPoint3x4(corner);
                    if (!has) { accBounds = new Bounds(p, Vector3.zero); has = true; }
                    else accBounds.Encapsulate(p);
                }
            }

            foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
                Accumulate(mf.sharedMesh, mf.transform);
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>())
                Accumulate(smr.sharedMesh, smr.transform);

            bounds = accBounds;
            return has;
        }

        void GenerateAndSave(bool assignToSelection)
        {
            List<Vector2> pts = BuildShape();          // [0,1] 볼록 폴리곤 (CCW)
            Mesh mesh = BuildMesh(pts, m_size, m_flipWinding);
            mesh.name = System.IO.Path.GetFileNameWithoutExtension(m_savePath);

            // 기존 에셋이 있으면 덮어쓰기(참조 유지)
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(m_savePath);
            if (existing != null)
            {
                existing.Clear();
                existing.vertices = mesh.vertices;
                existing.uv = mesh.uv;
                existing.triangles = mesh.triangles;
                existing.normals = mesh.normals;
                existing.bounds = mesh.bounds;
                existing.name = mesh.name;
                EditorUtility.SetDirty(existing);
                mesh = existing;
            }
            else
            {
                AssetDatabase.CreateAsset(mesh, m_savePath);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (assignToSelection && Selection.activeGameObject != null)
            {
                var mf = Selection.activeGameObject.GetComponent<MeshFilter>();
                if (mf == null) mf = Undo.AddComponent<MeshFilter>(Selection.activeGameObject);
                Undo.RecordObject(mf, "Assign Impostor Mesh");
                mf.sharedMesh = mesh;
                EditorUtility.SetDirty(mf);
            }

            Debug.Log($"[ImpostorMeshGenerator] '{m_savePath}' 생성 완료 " +
                      $"(verts: {mesh.vertexCount}, tris: {mesh.triangles.Length / 3}). " +
                      $"머티리얼 Impostor Size = {m_size} 로 맞추세요.");
            Selection.activeObject = mesh;
        }

        // [0,1] 공간의 볼록 폴리곤 점들을 CCW(수학좌표, y up) 순서로 생성
        List<Vector2> BuildShape()
        {
            var p = new List<Vector2>();
            switch (m_shape)
            {
                case ShapeType.Quad:
                    p.Add(new Vector2(0, 0));
                    p.Add(new Vector2(1, 0));
                    p.Add(new Vector2(1, 1));
                    p.Add(new Vector2(0, 1));
                    break;

                case ShapeType.Octagon: // Amplify 기본 팔각형
                    float c = m_cornerCut;
                    p.Add(new Vector2(c, 0));
                    p.Add(new Vector2(1 - c, 0));
                    p.Add(new Vector2(1, c));
                    p.Add(new Vector2(1, 1 - c));
                    p.Add(new Vector2(1 - c, 1));
                    p.Add(new Vector2(c, 1));
                    p.Add(new Vector2(0, 1 - c));
                    p.Add(new Vector2(0, c));
                    break;

                case ShapeType.NGon: // 정다각형(원에 내접)
                    for (int i = 0; i < m_nGonSides; i++)
                    {
                        float a = Mathf.PI * 2f * i / m_nGonSides + Mathf.PI * 0.5f;
                        p.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(a), 0.5f + 0.5f * Mathf.Sin(a)));
                    }
                    break;
            }
            return p;
        }

        // 볼록 폴리곤 → 중앙정렬 메시. 정점 xy = (point - 0.5) * size, z = 0. 팬 삼각분할.
        static Mesh BuildMesh(List<Vector2> pts, float size, bool flip)
        {
            int n = pts.Count;
            var vertices = new Vector3[n];
            var uv = new Vector2[n];
            var normals = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                vertices[i] = new Vector3((pts[i].x - 0.5f) * size, (pts[i].y - 0.5f) * size, 0f);
                uv[i] = pts[i];                       // 디버그용(런타임 미사용)
                normals[i] = flip ? Vector3.back : Vector3.forward;
            }

            // 볼록 폴리곤이므로 fan 삼각분할(0, i, i+1)로 충분
            var tris = new List<int>((n - 2) * 3);
            for (int i = 1; i < n - 1; i++)
            {
                if (!flip) { tris.Add(0); tris.Add(i); tris.Add(i + 1); }
                else       { tris.Add(0); tris.Add(i + 1); tris.Add(i); }
            }

            var mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.normals = normals;
            mesh.triangles = tris.ToArray();
            float half = size * 0.5f;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(size, size, half));
            return mesh;
        }
    }
}
