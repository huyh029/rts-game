using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using RecoilPathfinding;
using Unity.Entities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RecoilPathfindingEditor
{
    public static class RecoilAnimationPerformanceChecks
    {
        [MenuItem("Tools/Recoil Pathfinding/Validate Character Animation")]
        public static void Validate()
        {
            RecoilCharacterPresentationScheduler.ValidateRegistry();
            RecoilUnitPresentationSync.Validate();
            RecoilUnitPresentationSync.ValidateRotationSmoothing();
            UnityEngine.Debug.Log("Character animation checks passed. " + Benchmark());
        }

        public static string Benchmark()
        {
            using var world = new World("Animation wrapper benchmark");
            var scene = EditorSceneManager.NewPreviewScene();
            var views = new List<RecoilCharacterPresentation>();
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Data/Characters/Prefabs/BO_BINH_blue.prefab");
                for (var i = 0; i < 64; i++)
                {
                    var clone = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    var view = clone.GetComponent<RecoilCharacterPresentation>();
                    if (view == null || view.VisualRoot == null || view.Animator == null)
                        throw new InvalidOperationException("Benchmark prefab must have a configured visual root and Animator.");
                    var agent = clone.GetComponent<RecoilNavMeshPathAgent>() ?? clone.AddComponent<RecoilNavMeshPathAgent>();
                    var entity = world.EntityManager.CreateEntity();
                    var simulation = new RecoilUnitSimulation(world, entity, null, Vector3.zero, Quaternion.identity, agent.MovementSettings);
                    agent.Bind(simulation);
                    view.ResetForReuse();
                    views.Add(view);
                }
                var update = (Action<RecoilCharacterPresentation>)Delegate.CreateDelegate(typeof(Action<RecoilCharacterPresentation>),
                    typeof(RecoilCharacterPresentation).GetMethod("UpdateLocomotionAnimation", BindingFlags.Instance | BindingFlags.NonPublic));
                var sample = views[0];
                if (!sample.Animator.isActiveAndEnabled)
                    throw new InvalidOperationException("Benchmark Animator is inactive; results would not measure animation work.");
                sample.SetPresentationMovementSpeed(3.5f);
                update(sample);
                sample.Animator.Update(0.3f);
                if (!sample.Animator.GetCurrentAnimatorStateInfo(0).IsName("Run"))
                    throw new InvalidOperationException("Movement snapshot did not select Run.");
                if (!sample.PlayAttack(null)) throw new InvalidOperationException("Attack could not start.");
                sample.Animator.Update(0.1f);
                sample.SetPresentationMovementSpeed(0f);
                update(sample);
                if (!sample.Animator.GetCurrentAnimatorStateInfo(0).IsName("Attack") &&
                    !sample.Animator.GetNextAnimatorStateInfo(0).IsName("Attack"))
                    throw new InvalidOperationException("Locomotion interrupted Attack.");
                if (!sample.HasPendingAnimationWork)
                    throw new InvalidOperationException("Attack must remain scheduled until it finishes.");
                sample.Animator.Update(10f);
                sample.Animator.Update(.3f);
                typeof(RecoilCharacterPresentation).GetField("_attackEntryDeadline", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(sample, 0f);
                update(sample);
                if (sample.HasPendingAnimationWork)
                    throw new InvalidOperationException("Finished Attack must release its animation task.");
                var tick = (Action<RecoilCharacterPresentation>)Delegate.CreateDelegate(typeof(Action<RecoilCharacterPresentation>),
                    typeof(RecoilCharacterPresentation).GetMethod("TickPresentation", BindingFlags.Instance | BindingFlags.NonPublic));
                var deathCallbacks = 0;
                sample.PlayDeath(() => deathCallbacks++);
                if (!sample.HasPendingAnimationWork)
                    throw new InvalidOperationException("Death must remain scheduled until completion.");
                foreach (var child in sample.VisualRoot.GetComponentsInChildren<Animator>(true))
                {
                    if (child.runtimeAnimatorController == null || !child.HasState(0, Animator.StringToHash("Base Layer.Death"))) continue;
                    child.Play("Base Layer.Death", 0, 1.01f);
                    child.Update(0f);
                }
                tick(sample);
                tick(sample);
                sample.SetPresentationMovementSpeed(3.5f);
                if (sample.HasPendingAnimationWork || deathCallbacks != 1)
                    throw new InvalidOperationException("Completed Death must sleep and invoke its callback exactly once.");
                sample.ResetForReuse();
                foreach (var view in views)
                {
                    view.SetPresentationMovementSpeed(0f);
                    update(view);
                    if (view.HasPendingAnimationWork)
                        throw new InvalidOperationException("Stable Idle must have no scheduled animation work.");
                    view.SetPresentationMovementSpeed(0f);
                    if (view.HasPendingAnimationWork)
                        throw new InvalidOperationException("Unchanged movement must not wake animation work.");
                    view.SetPresentationMovementSpeed(3.5f);
                    if (!view.HasPendingAnimationWork)
                        throw new InvalidOperationException("Starting movement must wake animation work.");
                    update(view);
                    if (view.HasPendingAnimationWork)
                        throw new InvalidOperationException("Stable Run must have no scheduled animation work.");
                }
                for (var pass = 0; pass < 10; pass++) foreach (var view in views) update(view);
                var allocationProbeStart = GC.GetAllocatedBytesForCurrentThread();
                var allocationProbe = new byte[1024];
                GC.KeepAlive(allocationProbe);
                var allocationCounterAvailable = GC.GetAllocatedBytesForCurrentThread() > allocationProbeStart;
                var watch = new Stopwatch();
                var before = GC.GetAllocatedBytesForCurrentThread();
                watch.Start();
                for (var pass = 0; pass < 300; pass++) foreach (var view in views) update(view);
                watch.Stop();
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                var allocationReport = allocationCounterAvailable ? $"{allocated} bytes" : "counter unavailable";
                return $"64 units x 300 calls (19200): C# locomotion wrapper {watch.Elapsed.TotalMilliseconds:F3} ms; allocated {allocationReport}. Animator engine evaluation/rendering excluded; repeated same-frame calls measure the stable locomotion path, not attack/death or full-frame performance.";
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
    }
}
