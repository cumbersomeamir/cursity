using System.IO;
using UnityEngine;

public class DnaReplicationRender
{
    // Called by Unity via -executeMethod DnaReplicationRender.Render
    public static void Render()
    {
        Directory.CreateDirectory("outputs/frames");

        // Output settings
        int width = 720;
        int height = 1280;
        int fps = 30;
        int totalFrames = fps * 4;

        // Camera
        var camGO = new GameObject("RenderCam");
        var cam = camGO.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.03f, 0.04f, 0.06f, 1f);
        cam.fieldOfView = 35f;
        cam.nearClipPlane = 0.03f;
        cam.farClipPlane = 250f;

        // Light rig
        var keyGO = new GameObject("KeyLight");
        var key = keyGO.AddComponent<Light>();
        key.type = LightType.Directional;
        key.intensity = 1.2f;
        keyGO.transform.rotation = Quaternion.Euler(45f, 25f, 0f);

        var fillGO = new GameObject("FillLight");
        var fill = fillGO.AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.intensity = 0.6f;
        fillGO.transform.rotation = Quaternion.Euler(130f, 220f, 0f);

        // Shader selection (URP first)
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        bool isUrp = shader != null;
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) throw new System.Exception("No suitable shader found.");

        Material MakeMat(Color baseCol, Color emission, bool useEmission, float metallic = 0.0f, float smoothness = 0.75f)
        {
            var m = new Material(shader);
            m.enableInstancing = true;

            // Base color
            m.color = baseCol;
            m.SetColor(isUrp ? "_BaseColor" : "_Color", baseCol);

            // Metallic/smoothness if supported
            // URP Lit: _Metallic, _Smoothness
            // Built-in Standard: _Metallic, _Glossiness
            m.SetFloat("_Metallic", metallic);
            m.SetFloat(isUrp ? "_Smoothness" : "_Glossiness", smoothness);

            // Emission
            if (useEmission)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission);
            }
            return m;
        }

        // Palette
        // Keep emissions subtle to avoid blown-out frames in URP.
        var backboneA = MakeMat(new Color(0.20f, 0.80f, 0.85f, 1f), new Color(0.02f, 0.08f, 0.10f, 1f), true, 0.0f, 0.70f);
        var backboneB = MakeMat(new Color(0.85f, 0.30f, 0.75f, 1f), new Color(0.08f, 0.02f, 0.07f, 1f), true, 0.0f, 0.70f);

        // Base-pair rungs: color-coded, mostly non-emissive for clarity.
        var rungAT = MakeMat(new Color(0.98f, 0.78f, 0.25f, 1f), new Color(0, 0, 0, 1f), false, 0.0f, 0.65f); // A-T
        var rungCG = MakeMat(new Color(0.35f, 1.00f, 0.55f, 1f), new Color(0, 0, 0, 1f), false, 0.0f, 0.65f); // C-G
        var rungGC = MakeMat(new Color(1.00f, 0.35f, 0.35f, 1f), new Color(0, 0, 0, 1f), false, 0.0f, 0.65f); // G-C
        var rungTA = MakeMat(new Color(0.30f, 0.70f, 1.00f, 1f), new Color(0, 0, 0, 1f), false, 0.0f, 0.65f); // T-A

        // Enzymes: slightly glossy, very low emission (avoid overexposure)
        var enzymeMat = MakeMat(new Color(0.78f, 0.82f, 0.90f, 1f), new Color(0.03f, 0.04f, 0.06f, 1f), true, 0.0f, 0.85f);

        // Use mesh from primitives (instanced draws)
        Mesh sphereMesh;
        Mesh capsuleMesh;
        Mesh cylinderMesh;
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(tmp.GetComponent<Collider>());
            sphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
        }
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.DestroyImmediate(tmp.GetComponent<Collider>());
            capsuleMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
        }
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.DestroyImmediate(tmp.GetComponent<Collider>());
            cylinderMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
        }

        // DNA geometry params
        int pairs = 90;
        float pitch = 0.12f;      // spacing along Y
        float helixR = 0.45f;
        float twist = 0.55f;      // radians per step
        float rungLen = 0.65f;
        float rungThick = 0.020f;
        float bead = 0.055f;

        // Two daughter helices separation
        float daughterSep = 0.95f;
        float transition = 0.55f;

        // Render target
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        rt.antiAliasing = 2;
        cam.targetTexture = rt;
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);

        // Instance buffers
        var matsA = new Matrix4x4[1023];
        var matsB = new Matrix4x4[1023];
        var matsRungAT = new Matrix4x4[1023];
        var matsRungCG = new Matrix4x4[1023];
        var matsRungGC = new Matrix4x4[1023];
        var matsRungTA = new Matrix4x4[1023];

        // Helper draws
        void Flush(Mesh mesh, Material mat, Matrix4x4[] buf, int count)
        {
            if (count <= 0) return;
            Graphics.DrawMeshInstanced(mesh, 0, mat, buf, count);
        }

        // Base pairing -> rung material
        Material PickRungMat(int i)
        {
            // simple repeating sequence: A-T, C-G, G-C, T-A
            int k = i % 4;
            if (k == 0) return rungAT;
            if (k == 1) return rungCG;
            if (k == 2) return rungGC;
            return rungTA;
        }

        // Animation loop
        for (int f = 0; f < totalFrames; f++)
        {
            float t = f / (float)(totalFrames - 1);

            // Replication fork moves upward through the helix
            float yMin = -(pairs * pitch) * 0.5f;
            float yMax = (pairs * pitch) * 0.5f;
            float forkY = Mathf.Lerp(yMin + 0.6f, yMax - 0.6f, t);

            // Camera: gentle orbit + slight zoom
            float orbit = Mathf.Lerp(-18f, 28f, t);
            float dist = Mathf.Lerp(4.2f, 5.2f, t);
            camGO.transform.position = Quaternion.Euler(12f, orbit, 0f) * new Vector3(0f, 0.25f, -dist);
            camGO.transform.LookAt(new Vector3(0f, 0.0f, 0f));

            // Global rotation of helix for motion
            float spin = t * 120f;
            Quaternion globalRot = Quaternion.Euler(0f, spin, 0f);

            RenderTexture.active = rt;
            GL.Clear(true, true, cam.backgroundColor);

            // Enzymes around the fork (helicase + polymerases)
            {
                // helicase ring-ish: 6 spheres around fork
                float ringR = 0.28f;
                for (int k = 0; k < 6; k++)
                {
                    float a = k * (Mathf.PI * 2f / 6f) + t * 6f;
                    Vector3 p = new Vector3(Mathf.Cos(a) * ringR, forkY, Mathf.Sin(a) * ringR);
                    p = globalRot * p;
                    Graphics.DrawMesh(sphereMesh, Matrix4x4.TRS(p, Quaternion.identity, Vector3.one * 0.10f), enzymeMat, 0);
                }

                // polymerases: two capsules slightly behind fork on each daughter
                float behind = forkY - 0.35f;
                float wob = 0.05f * Mathf.Sin(t * 10f);
                Vector3 pL = globalRot * new Vector3(-daughterSep * 0.45f, behind, wob);
                Vector3 pR = globalRot * new Vector3(+daughterSep * 0.45f, behind, -wob);
                Graphics.DrawMesh(capsuleMesh, Matrix4x4.TRS(pL, Quaternion.Euler(90, 0, 20), Vector3.one * 0.20f), enzymeMat, 0);
                Graphics.DrawMesh(capsuleMesh, Matrix4x4.TRS(pR, Quaternion.Euler(90, 0, -20), Vector3.one * 0.20f), enzymeMat, 0);
            }

            // Build DNA segments with a fork transition
            int aCount = 0, bCount = 0;
            int rungATCount = 0, rungCGCount = 0, rungGCCount = 0, rungTACount = 0;

            for (int i = 0; i < pairs; i++)
            {
                float yy = Mathf.Lerp(yMin, yMax, i / (float)(pairs - 1));

                // region blend: 0 = daughter (below fork), 1 = original (above fork)
                float blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(forkY - transition, forkY + transition, yy));

                // daughter separation increases as you go below fork
                float sep = (1f - blend) * daughterSep;

                // local helix phase
                float ang = i * twist + t * 2.2f;

                // Original helix center (x=0) and two daughter centers (x=±sep/2)
                float centerX_L = Mathf.Lerp(-sep * 0.5f, 0f, blend);
                float centerX_R = Mathf.Lerp(+sep * 0.5f, 0f, blend);

                // For original helix (blend≈1): strands are opposite on same helix
                // For daughter region (blend≈0): we create TWO helices, each with opposite strands.

                // Decide which structure to use
                bool isOriginal = blend > 0.5f;

                // Backbone beads + rungs
                if (isOriginal)
                {
                    Vector3 c = globalRot * new Vector3(0f, yy, 0f);
                    Vector3 s1 = globalRot * (new Vector3(Mathf.Cos(ang) * helixR, yy, Mathf.Sin(ang) * helixR));
                    Vector3 s2 = globalRot * (new Vector3(Mathf.Cos(ang + Mathf.PI) * helixR, yy, Mathf.Sin(ang + Mathf.PI) * helixR));

                    matsA[aCount++] = Matrix4x4.TRS(s1, Quaternion.identity, Vector3.one * bead);
                    matsB[bCount++] = Matrix4x4.TRS(s2, Quaternion.identity, Vector3.one * bead);

                    // rung (base pair) between strands
                    Vector3 mid = (s1 + s2) * 0.5f;
                    Vector3 dir = (s2 - s1);
                    float len = dir.magnitude;
                    Quaternion rot = Quaternion.FromToRotation(Vector3.up, dir.normalized);
                    var rungMat = PickRungMat(i);
                    var rungMtx = Matrix4x4.TRS(mid, rot, new Vector3(rungThick, len * 0.5f, rungThick));
                    if (rungMat == rungAT) matsRungAT[rungATCount++] = rungMtx;
                    else if (rungMat == rungCG) matsRungCG[rungCGCount++] = rungMtx;
                    else if (rungMat == rungGC) matsRungGC[rungGCCount++] = rungMtx;
                    else matsRungTA[rungTACount++] = rungMtx;

                    // flush when near buffer limits
                    if (aCount >= 1023) { Flush(sphereMesh, backboneA, matsA, aCount); aCount = 0; }
                    if (bCount >= 1023) { Flush(sphereMesh, backboneB, matsB, bCount); bCount = 0; }
                    if (rungATCount >= 1023) { Flush(cylinderMesh, rungAT, matsRungAT, rungATCount); rungATCount = 0; }
                    if (rungCGCount >= 1023) { Flush(cylinderMesh, rungCG, matsRungCG, rungCGCount); rungCGCount = 0; }
                    if (rungGCCount >= 1023) { Flush(cylinderMesh, rungGC, matsRungGC, rungGCCount); rungGCCount = 0; }
                    if (rungTACount >= 1023) { Flush(cylinderMesh, rungTA, matsRungTA, rungTACount); rungTACount = 0; }
                }
                else
                {
                    // Two daughter helices diverge. Each helix has two strands.
                    // Helix L
                    float angL = ang + 0.4f;
                    Vector3 s1L = globalRot * (new Vector3(centerX_L + Mathf.Cos(angL) * helixR, yy, Mathf.Sin(angL) * helixR));
                    Vector3 s2L = globalRot * (new Vector3(centerX_L + Mathf.Cos(angL + Mathf.PI) * helixR, yy, Mathf.Sin(angL + Mathf.PI) * helixR));

                    // Helix R
                    float angR = -ang + 0.8f;
                    Vector3 s1R = globalRot * (new Vector3(centerX_R + Mathf.Cos(angR) * helixR, yy, Mathf.Sin(angR) * helixR));
                    Vector3 s2R = globalRot * (new Vector3(centerX_R + Mathf.Cos(angR + Mathf.PI) * helixR, yy, Mathf.Sin(angR + Mathf.PI) * helixR));

                    // backbones
                    matsA[aCount++] = Matrix4x4.TRS(s1L, Quaternion.identity, Vector3.one * bead);
                    matsB[bCount++] = Matrix4x4.TRS(s2L, Quaternion.identity, Vector3.one * bead);
                    matsA[aCount++] = Matrix4x4.TRS(s1R, Quaternion.identity, Vector3.one * bead);
                    matsB[bCount++] = Matrix4x4.TRS(s2R, Quaternion.identity, Vector3.one * bead);

                    // new base pairs appear behind polymerase (a bit below fork)
                    float build = Mathf.Clamp01((forkY - yy) / 0.9f);
                    if (build > 0.08f)
                    {
                        // rungs for both daughter helices (color-coded)
                        var rungMat = PickRungMat(i);
                        Vector3 midL = (s1L + s2L) * 0.5f;
                        Vector3 dirL = (s2L - s1L);
                        float lenL = dirL.magnitude;
                        var rungMtxL = Matrix4x4.TRS(midL, Quaternion.FromToRotation(Vector3.up, dirL.normalized), new Vector3(rungThick, lenL * 0.5f, rungThick));
                        if (rungMat == rungAT) matsRungAT[rungATCount++] = rungMtxL;
                        else if (rungMat == rungCG) matsRungCG[rungCGCount++] = rungMtxL;
                        else if (rungMat == rungGC) matsRungGC[rungGCCount++] = rungMtxL;
                        else matsRungTA[rungTACount++] = rungMtxL;

                        Vector3 midR = (s1R + s2R) * 0.5f;
                        Vector3 dirR = (s2R - s1R);
                        float lenR = dirR.magnitude;
                        var rungMtxR = Matrix4x4.TRS(midR, Quaternion.FromToRotation(Vector3.up, dirR.normalized), new Vector3(rungThick, lenR * 0.5f, rungThick));
                        if (rungMat == rungAT) matsRungAT[rungATCount++] = rungMtxR;
                        else if (rungMat == rungCG) matsRungCG[rungCGCount++] = rungMtxR;
                        else if (rungMat == rungGC) matsRungGC[rungGCCount++] = rungMtxR;
                        else matsRungTA[rungTACount++] = rungMtxR;
                    }

                    if (aCount >= 1023) { Flush(sphereMesh, backboneA, matsA, aCount); aCount = 0; }
                    if (bCount >= 1023) { Flush(sphereMesh, backboneB, matsB, bCount); bCount = 0; }
                    if (rungATCount >= 1023) { Flush(cylinderMesh, rungAT, matsRungAT, rungATCount); rungATCount = 0; }
                    if (rungCGCount >= 1023) { Flush(cylinderMesh, rungCG, matsRungCG, rungCGCount); rungCGCount = 0; }
                    if (rungGCCount >= 1023) { Flush(cylinderMesh, rungGC, matsRungGC, rungGCCount); rungGCCount = 0; }
                    if (rungTACount >= 1023) { Flush(cylinderMesh, rungTA, matsRungTA, rungTACount); rungTACount = 0; }
                }
            }

            // Flush remaining
            Flush(sphereMesh, backboneA, matsA, aCount);
            Flush(sphereMesh, backboneB, matsB, bCount);
            Flush(cylinderMesh, rungAT, matsRungAT, rungATCount);
            Flush(cylinderMesh, rungCG, matsRungCG, rungCGCount);
            Flush(cylinderMesh, rungGC, matsRungGC, rungGCCount);
            Flush(cylinderMesh, rungTA, matsRungTA, rungTACount);

            // Render
            cam.Render();

            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply(false);

            File.WriteAllBytes($"outputs/frames/frame_{f:D04}.png", tex.EncodeToPNG());
        }

        // Cleanup
        cam.targetTexture = null;
        RenderTexture.active = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(tex);

        #if UNITY_EDITOR
        UnityEditor.EditorApplication.Exit(0);
        #else
        Application.Quit(0);
        #endif
    }
}
