// Re-enable this optional renderer by restoring the plugin and defining SKGAMES_ANIMATED_IMPOSTOR.
#if SKGAMES_ANIMATED_IMPOSTOR
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SKGames.AnimatedImpostor;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.Rendering;
using Unity.Profiling;

namespace RecoilPathfinding
{
    [DefaultExecutionOrder(200)]
    public sealed class RecoilGpuImpostorRenderer : MonoBehaviour
    {
        [SerializeField, Min(0f)] private float maxRenderDistance = 0f; // Zero keeps all distances.
        private readonly Dictionary<(Mesh, Material, int), Group> _groups = new();
        private readonly Dictionary<AnimatedImpostorPlayer, Group> _players = new();
        private readonly Plane[] _planes = new Plane[6];
        private readonly Vector4[] _planeVectors = new Vector4[6];
        private ComputeShader _culling;
        private int _kernel;
        private int _uploadedFrame = -1;
        private static readonly ProfilerMarker CaptureMarker = new("RecoilBattle.Rendering.GpuCapture");
        private static readonly ProfilerMarker UploadMarker = new("RecoilBattle.Rendering.GpuUpload");
        private static readonly ProfilerMarker CullDrawMarker = new("RecoilBattle.Rendering.GpuCullDraw");
        public int LastDrawCallCount { get; private set; }
        public int RegisteredCount => _players.Count;

        [StructLayout(LayoutKind.Sequential)]
        private struct Instance
        {
            public Matrix4x4 ObjectToWorld, WorldToObject;
            public Vector4 Playback, Options, Flags;
        }

        [BurstCompile]
        private struct CaptureTransforms : IJobParallelForTransform
        {
            public NativeArray<Instance> Data;
            public void Execute(int index, TransformAccess transform)
            {
                var value = Data[index];
                if (value.Flags.x < 0.5f || !transform.isValid) return;
                value.ObjectToWorld = transform.localToWorldMatrix;
                value.WorldToObject = transform.worldToLocalMatrix;
                Data[index] = value;
            }
        }

        public bool TryRegister(GameObject unit)
        {
            if (!isActiveAndEnabled || !SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing ||
                SystemInfo.graphicsShaderLevel < 45) return false;
            if (_culling == null)
            {
                var source = Resources.Load<ComputeShader>("RecoilGpuImpostorCulling");
                if (source == null) return false;
                _culling = Instantiate(source);
                _kernel = _culling.FindKernel("Cull");
            }
            var found = false;
            var players = unit.GetComponentsInChildren<AnimatedImpostorPlayer>(true);
            foreach (var player in players)
            {
                if (_players.ContainsKey(player)) { found = true; continue; }
                var mesh = player.GetComponent<MeshFilter>();
                var renderer = player.GetComponent<MeshRenderer>();
                if (player.Library == null || mesh == null || mesh.sharedMesh == null || mesh.sharedMesh.subMeshCount != 1 ||
                    renderer == null || renderer.sharedMaterial == null ||
                    renderer.sharedMaterial.shader.name != "SKGames/Animated Impostor" || !renderer.sharedMaterial.shader.isSupported) continue;
                var key = (mesh.sharedMesh, renderer.sharedMaterial, player.gameObject.layer);
                if (!_groups.TryGetValue(key, out var group))
                {
                    group = new Group(key.Item1, key.Item2, key.Item3);
                    _groups.Add(key, group);
                }
                group.Add(player);
                _players.Add(player, group);
                player.PlaybackChanged += OnPlaybackChanged;
                player.PlayerDestroyed += OnPlayerDestroyed;
                player.SetIndirectRendering(true);
                _uploadedFrame = -1;
                found = true;
            }
            return found;
        }

        private void OnPlaybackChanged(AnimatedImpostorPlayer player)
        {
            if (_players.TryGetValue(player, out var group)) group.Refresh(player);
        }

        private void OnPlayerDestroyed(AnimatedImpostorPlayer player)
        {
            if (!_players.TryGetValue(player, out var group)) return;
            player.PlaybackChanged -= OnPlaybackChanged;
            player.PlayerDestroyed -= OnPlayerDestroyed;
            group.Remove(player);
            _players.Remove(player);
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += BeginCamera;
            Camera.onPreCull += BeginBuiltInCamera;
            // Rebuild once after re-enable/domain reload; native resources are released on disable.
            if (Application.isPlaying)
                foreach (var player in GetComponentsInChildren<AnimatedImpostorPlayer>(true))
                {
                    var view = player.GetComponentInParent<RecoilNavMeshPathAgent>(true);
                    // Inactive spawn templates must keep their renderer settings for cloning.
                    if (view != null && view.Simulation != null) TryRegister(player.gameObject);
                }
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= BeginCamera;
            Camera.onPreCull -= BeginBuiltInCamera;
            _uploadedFrame = -1;
            Release();
        }

        private void LateUpdate()
        {
            LastDrawCallCount = 0;
            if (_culling == null) return;
            // Gather all teams before waiting, then upload native memory once per group.
            using (CaptureMarker.Auto())
            {
                var pending = default(JobHandle);
                foreach (var group in _groups.Values) pending = JobHandle.CombineDependencies(pending, group.Capture());
                pending.Complete();
            }
            using (UploadMarker.Auto())
                foreach (var group in _groups.Values) group.Upload();
            _uploadedFrame = Time.frameCount;
        }

        private void BeginBuiltInCamera(Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline == null) Render(camera);
        }
        private void BeginCamera(ScriptableRenderContext context, Camera camera) => Render(camera);

        private void Render(Camera camera)
        {
            if (_culling == null || _uploadedFrame != Time.frameCount || camera.cameraType == CameraType.Preview ||
                camera.cameraType == CameraType.Reflection) return;
            GeometryUtility.CalculateFrustumPlanes(camera, _planes);
            for (var i = 0; i < 6; i++) _planeVectors[i] = new Vector4(_planes[i].normal.x, _planes[i].normal.y, _planes[i].normal.z, _planes[i].distance);
            _culling.SetVectorArray("_Planes", _planeVectors);
            _culling.SetVector("_CameraPosition", camera.transform.position);
            _culling.SetFloat("_MaxDistance", maxRenderDistance);
            using (CullDrawMarker.Auto())
                foreach (var group in _groups.Values)
                    if (group.Draw(_culling, _kernel, camera) && camera.cameraType == CameraType.Game) LastDrawCallCount++;
        }

        private void OnDestroy() => Release();

        private void Release()
        {
            foreach (var player in _players.Keys)
            {
                if (player == null) continue;
                player.PlaybackChanged -= OnPlaybackChanged;
                player.PlayerDestroyed -= OnPlayerDestroyed;
                player.SetIndirectRendering(false);
            }
            foreach (var group in _groups.Values) group.Dispose();
            _groups.Clear(); _players.Clear();
            if (_culling != null) ReleaseObject(_culling);
            _culling = null;
            LastDrawCallCount = 0;
        }

        private static void ReleaseObject(Object value)
        {
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }

        public static void ValidateGpuCulling()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing)
                throw new System.InvalidOperationException("Compute shaders and instancing are required.");
            if (Marshal.SizeOf<Instance>() != 176) throw new System.InvalidOperationException("GPU instance layout must be 176 bytes.");
            var source = Resources.Load<ComputeShader>("RecoilGpuImpostorCulling");
            if (source == null) throw new System.InvalidOperationException("Missing impostor culling compute shader.");
            var shader = Instantiate(source);
            using var instances = new ComputeBuffer(4, 176, ComputeBufferType.Structured);
            using var visible = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.Append);
            using var args = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
            try
            {
                var data = new Instance[4];
                for (var i = 0; i < data.Length; i++)
                {
                    var position = new Vector3(i == 1 ? 100f : i == 3 ? 10.25f : 0f, 0f, 5f);
                    data[i] = new Instance { ObjectToWorld = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one),
                        WorldToObject = Matrix4x4.TRS(-position, Quaternion.identity, Vector3.one), Flags = new Vector4(i == 2 ? 0f : 1f, 0f, 0f, 0f) };
                }
                instances.SetData(data);
                args.SetData(new uint[] { 6, 0, 0, 0, 0 });
                var kernel = shader.FindKernel("Cull");
                shader.SetVectorArray("_Planes", new[] { new Vector4(1,0,0,10), new Vector4(-1,0,0,10),
                    new Vector4(0,1,0,10), new Vector4(0,-1,0,10), new Vector4(0,0,1,0), new Vector4(0,0,-1,10) });
                shader.SetVector("_LocalSphere", new Vector4(0, 0, 0, 0.5f));
                shader.SetVector("_CameraPosition", Vector4.zero); shader.SetFloat("_MaxDistance", 0f);
                shader.SetInt("_Count", 4);
                shader.SetBuffer(kernel, "_RecoilInstances", instances); shader.SetBuffer(kernel, "_RecoilVisible", visible);
                visible.SetCounterValue(0); shader.Dispatch(kernel, 1, 1, 1);
                ComputeBuffer.CopyCount(visible, args, sizeof(uint));
                // Blocking reads are confined to this explicit diagnostic, never the render loop.
                var commands = new uint[5]; args.GetData(commands);
                var indices = new uint[4]; visible.GetData(indices);
                if (commands[0] != 6 || commands[1] != 2 ||
                    !((indices[0] == 0 && indices[1] == 3) || (indices[0] == 3 && indices[1] == 0)))
                    throw new System.InvalidOperationException("GPU culling must retain visible/partial spheres, reject inactive/outside units and preserve indirect index arguments.");
                visible.SetCounterValue(0); shader.SetFloat("_MaxDistance", 1f); shader.Dispatch(kernel, 1, 1, 1);
                ComputeBuffer.CopyCount(visible, args, sizeof(uint)); args.GetData(commands);
                if (commands[1] != 0) throw new System.InvalidOperationException("Distance culling must honor the configured limit and reset the counter.");
            }
            finally { DestroyImmediate(shader); }
        }

        private sealed class Group
        {
            private readonly Mesh _mesh;
            private readonly Material _material;
            private readonly int _layer;
            private readonly List<AnimatedImpostorPlayer> _players = new();
            private readonly Dictionary<AnimatedImpostorPlayer, int> _indices = new();
            private TransformAccessArray _transforms = new(16);
            private NativeArray<Instance> _data;
            private ComputeBuffer _instances, _visible, _args;
            private readonly MaterialPropertyBlock _properties = new();
            private readonly Vector4 _sphere;

            public Group(Mesh mesh, Material material, int layer)
            {
                _mesh = mesh; _layer = layer;
                _material = new Material(material) { name = material.name + " GPU indirect", enableInstancing = true };
                var billboard = material.GetVector("_Billboard");
                _sphere = new Vector4(billboard.x, billboard.y, billboard.z, Mathf.Max(0.01f, Mathf.Abs(billboard.w) * 0.9f));
            }

            public void Add(AnimatedImpostorPlayer player)
            {
                _indices.Add(player, _players.Count);
                _players.Add(player); _transforms.Add(player.transform);
                if (!_data.IsCreated || _players.Count > _data.Length)
                {
                    var capacity = Mathf.NextPowerOfTwo(Mathf.Max(16, _players.Count));
                    var replacement = new NativeArray<Instance>(capacity, Allocator.Persistent);
                    if (_data.IsCreated)
                    {
                        NativeArray<Instance>.Copy(_data, replacement, _players.Count - 1);
                        _data.Dispose();
                    }
                    _data = replacement;
                    _instances?.Dispose(); _visible?.Dispose(); _args?.Dispose();
                    _instances = new ComputeBuffer(capacity, Marshal.SizeOf<Instance>(), ComputeBufferType.Structured);
                    _visible = new ComputeBuffer(capacity, sizeof(uint), ComputeBufferType.Append);
                    _args = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
                    _args.SetData(new uint[] { _mesh.GetIndexCount(0), 0, _mesh.GetIndexStart(0), _mesh.GetBaseVertex(0), 0 });
                    _properties.SetBuffer("_RecoilInstances", _instances);
                    _properties.SetBuffer("_RecoilVisible", _visible);
                }
                Refresh(player);
            }

            public void Refresh(AnimatedImpostorPlayer player)
            {
                var index = _indices[player];
                var value = _data[index];
                value.Playback = player.PlaybackData;
                value.Options = player.OptionsData;
                value.Flags.x = player.isActiveAndEnabled ? 1f : 0f;
                _data[index] = value;
            }

            public void Remove(AnimatedImpostorPlayer player)
            {
                var index = _indices[player]; var last = _players.Count - 1;
                if (index != last)
                {
                    _players[index] = _players[last]; _indices[_players[index]] = index;
                    _data[index] = _data[last];
                    // Destruction can occur after LateUpdate. Patch the swapped GPU slot
                    // instead of dropping the entire army for that frame.
                    _instances.SetData(_data, index, index, 1);
                }
                _players.RemoveAt(last); _indices.Remove(player); _transforms.RemoveAtSwapBack(index);
            }

            public JobHandle Capture() => _players.Count == 0 ? default :
                new CaptureTransforms { Data = _data }.Schedule(_transforms);
            public void Upload()
            {
                if (_players.Count > 0) _instances.SetData(_data, 0, 0, _players.Count);
            }

            public bool Draw(ComputeShader culling, int kernel, Camera camera)
            {
                if (_players.Count == 0 || (camera.cullingMask & (1 << _layer)) == 0) return false;
                _visible.SetCounterValue(0);
                culling.SetInt("_Count", _players.Count);
                culling.SetVector("_LocalSphere", _sphere);
                culling.SetBuffer(kernel, "_RecoilInstances", _instances);
                culling.SetBuffer(kernel, "_RecoilVisible", _visible);
                culling.Dispatch(kernel, (_players.Count + 63) / 64, 1, 1);
                ComputeBuffer.CopyCount(_visible, _args, sizeof(uint));
                Graphics.DrawMeshInstancedIndirect(_mesh, 0, _material,
                    new Bounds(camera.transform.position, Vector3.one * 1000000f), _args, 0, _properties,
                    ShadowCastingMode.Off, false, _layer, camera, LightProbeUsage.Off);
                return true;
            }

            public void Dispose()
            {
                if (_data.IsCreated) _data.Dispose();
                if (_transforms.isCreated) _transforms.Dispose();
                _instances?.Dispose(); _visible?.Dispose(); _args?.Dispose();
                ReleaseObject(_material);
            }
        }
    }
}
#endif
