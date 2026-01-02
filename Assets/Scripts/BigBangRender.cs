using System.IO;
using UnityEngine;

public class BigBangRender
{
    // Called by Unity via -executeMethod BigBangRender.Render
    public static void Render()
    {
        // Output
        Directory.CreateDirectory("outputs/frames");

        // Render settings
        int width = 720;
        int height = 1280;
        int fps = 30;
        int totalFrames = fps * 4;

        // Camera
        var camGO = new GameObject("RenderCam");
        var cam = camGO.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.01f, 0.01f, 0.02f, 1f);
        cam.fieldOfView = 45f;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 200f;

        // Light (subtle)
        var lightGO = new GameObject("KeyLight");
        var light = lightGO.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 0.6f;
        lightGO.transform.rotation = Quaternion.Euler(50, 25, 0);

        // Materials (URP Lit preferred; fall back to built-in if needed)
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        bool isUrp = shader != null;
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) throw new System.Exception("No suitable shader found (URP/Lit, Standard, Unlit/Color).");

        var matHot = new Material(shader);
        matHot.enableInstancing = true;
        var hotBase = new Color(1.0f, 0.75f, 0.25f, 1f);
        matHot.color = hotBase;
        matHot.SetColor(isUrp ? "_BaseColor" : "_Color", hotBase);
        matHot.EnableKeyword("_EMISSION");
        matHot.SetColor("_EmissionColor", new Color(4.0f, 1.8f, 0.4f, 1f));

        var matCool = new Material(shader);
        matCool.enableInstancing = true;
        var coolBase = new Color(0.3f, 0.7f, 1.0f, 1f);
        matCool.color = coolBase;
        matCool.SetColor(isUrp ? "_BaseColor" : "_Color", coolBase);
        matCool.EnableKeyword("_EMISSION");
        matCool.SetColor("_EmissionColor", new Color(0.5f, 1.5f, 3.5f, 1f));

        // Starfield backdrop (static points)
        int stars = 1200;
        var starMesh = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var starRenderer = starMesh.GetComponent<Renderer>();
        starRenderer.sharedMaterial = matCool;
        Object.DestroyImmediate(starMesh.GetComponent<Collider>());
        var starM = starMesh.GetComponent<MeshFilter>().sharedMesh;
        Object.DestroyImmediate(starMesh);

        // Big bang particles (expanding)
        int particles = 2500;
        var particleGO = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var particleRenderer = particleGO.GetComponent<Renderer>();
        particleRenderer.sharedMaterial = matHot;
        Object.DestroyImmediate(particleGO.GetComponent<Collider>());
        var particleM = particleGO.GetComponent<MeshFilter>().sharedMesh;
        Object.DestroyImmediate(particleGO);

        // Instance data
        var starPos = new Vector3[stars];
        var starScale = new float[stars];
        var starPhase = new float[stars];
        for (int i = 0; i < stars; i++)
        {
            // random points on a sphere shell
            float u = Random.value;
            float v = Random.value;
            float theta = 2f * Mathf.PI * u;
            float phi = Mathf.Acos(2f * v - 1f);
            float r = Random.Range(40f, 120f);
            starPos[i] = new Vector3(
                r * Mathf.Sin(phi) * Mathf.Cos(theta),
                r * Mathf.Cos(phi),
                r * Mathf.Sin(phi) * Mathf.Sin(theta)
            );
            starScale[i] = Random.Range(0.02f, 0.06f);
            starPhase[i] = Random.Range(0f, 10f);
        }

        var pDir = new Vector3[particles];
        var pSeed = new float[particles];
        for (int i = 0; i < particles; i++)
        {
            // random direction with slight clustering toward a disc
            float u = Random.value;
            float v = Random.value;
            float theta = 2f * Mathf.PI * u;
            float z = 2f * v - 1f;
            float w = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            Vector3 dir = new Vector3(w * Mathf.Cos(theta), z * 0.35f, w * Mathf.Sin(theta));
            pDir[i] = dir.normalized;
            pSeed[i] = Random.Range(0.2f, 1.0f);
        }

        // Render target
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        rt.antiAliasing = 2;
        cam.targetTexture = rt;

        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);

        // Matrices buffers (Unity draws instanced in batches of 1023)
        var starMatrices = new Matrix4x4[1023];
        var particleMatrices = new Matrix4x4[1023];

        // Animate
        for (int f = 0; f < totalFrames; f++)
        {
            float t = f / (float)(totalFrames - 1);

            // camera orbit + slow dolly back
            float orbit = Mathf.Lerp(-15f, 35f, t);
            float dist = Mathf.Lerp(6f, 10f, t);
            camGO.transform.position = Quaternion.Euler(10f, orbit, 0f) * new Vector3(0, 0.2f, -dist);
            camGO.transform.LookAt(Vector3.zero);

            // Clear explicitly
            RenderTexture.active = rt;
            GL.Clear(true, true, cam.backgroundColor);

            // Draw starfield with twinkle
            int si = 0;
            for (int i = 0; i < stars; i++)
            {
                float tw = 0.6f + 0.4f * Mathf.Sin(starPhase[i] + t * 12f);
                float s = starScale[i] * tw;
                starMatrices[si++] = Matrix4x4.TRS(starPos[i], Quaternion.identity, Vector3.one * s);
                if (si == 1023)
                {
                    Graphics.DrawMeshInstanced(starM, 0, matCool, starMatrices, si);
                    si = 0;
                }
            }
            if (si > 0)
                Graphics.DrawMeshInstanced(starM, 0, matCool, starMatrices, si);

            // Big bang expansion: radius grows rapidly then settles
            float expand = Mathf.Pow(t, 0.45f);
            float radius = Mathf.Lerp(0.02f, 12f, expand);
            float swirl = Mathf.Lerp(0f, 8f, t);

            int pi = 0;
            for (int i = 0; i < particles; i++)
            {
                // outward motion with gentle swirl
                Vector3 dir = pDir[i];
                float r = radius * (0.15f + 0.85f * pSeed[i]);
                float ang = swirl * (0.5f + pSeed[i]) + i * 0.002f;
                Quaternion q = Quaternion.Euler(0f, ang * Mathf.Rad2Deg, 0f);
                Vector3 pos = q * (dir * r);

                // particle size decreases as it expands
                float size = Mathf.Lerp(0.22f, 0.05f, t) * (0.7f + 0.6f * pSeed[i]);
                particleMatrices[pi++] = Matrix4x4.TRS(pos, Quaternion.identity, Vector3.one * size);

                if (pi == 1023)
                {
                    Graphics.DrawMeshInstanced(particleM, 0, matHot, particleMatrices, pi);
                    pi = 0;
                }
            }
            if (pi > 0)
                Graphics.DrawMeshInstanced(particleM, 0, matHot, particleMatrices, pi);

            // A bright initial flash (first ~0.3s)
            float flash = Mathf.Clamp01(1f - t * 3.5f);
            if (flash > 0f)
            {
                var flashMat = matHot;
                flashMat.SetColor("_EmissionColor", new Color(10f, 6f, 2f, 1f) * flash);
                Graphics.DrawMesh(particleM, Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * (0.6f + 2.5f * flash)), flashMat, 0);
                flashMat.SetColor("_EmissionColor", new Color(4.0f, 1.8f, 0.4f, 1f));
            }

            // Force draw now
            cam.Render();

            // Readback
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply(false);

            byte[] png = tex.EncodeToPNG();
            File.WriteAllBytes($"outputs/frames/frame_{f:D04}.png", png);
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
