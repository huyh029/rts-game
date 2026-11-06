using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using RecoilPathfinding;
using UnityEditor;
using UnityEngine;

namespace RecoilPathfindingEditor
{
    /// <summary>One-click correctness evaluation for the Recoil-converted runtime systems.</summary>
    public static class RecoilConversionEvaluation
    {
        [MenuItem("Tools/Recoil Pathfinding/Evaluate Converted Systems")]
        public static void Run()
        {
            if (!AreRecoilAssembliesCurrent())
                return;

            if (EditorApplication.isPlaying)
            {
                UnityEngine.Debug.LogWarning("Exit Play Mode before running deterministic conversion checks. For runtime effectiveness, use the battle profiler during a fixed-duration battle and compare the same map and unit count before/after.");
                return;
            }

            var passed = new List<string>();
            var failed = new List<string>();

            RunCheck("Fixed-step clock", RecoilUnitSimulationChecks.ValidateFixedSimulationClock, passed, failed);
            RunCheck("Presentation synchronization", RecoilUnitSimulationChecks.ValidatePresentationSync, passed, failed);
            RunCheck("Path scheduling and buffer reuse", RecoilUnitSimulationChecks.ValidatePathServiceScheduling, passed, failed);
            RunCheck("Team-specific castle path graphs", RecoilNavMeshPathService.ValidateCastleWalkability, passed, failed);
            RunCheck("Path funnel corner preservation", RecoilUnitSimulationChecks.ValidatePathFunnel, passed, failed);
            RunCheck("Stuck detection", RecoilUnitSimulationChecks.ValidateStuckDetection, passed, failed);
            RunCheck("Movement, pushing and body contacts", RecoilUnitSimulationChecks.ValidateRecoilMovement, passed, failed);
            RunCheck("Body contact steering", RecoilUnitSimulationChecks.ValidateBodyContacts, passed, failed);
            RunCheck("Crowd surface kernel", RecoilUnitSimulationChecks.ValidateCrowdSurfaceKernel, passed, failed);
            RunCheck("ECS state and presentation separation", RecoilUnitSimulationChecks.Run, passed, failed);
            RunCheck("Slope movement and surface height", RecoilUnitSimulationChecks.ValidateSlopeMovement, passed, failed);
            RunCheck("Combat target selection and Recoil weights", RecoilBattleCombatValidation.ValidateTargetSelection, passed, failed);
            RunCheck("Combat damage resolution and attribution", RecoilBattleCombatValidation.ValidateDamageResolution, passed, failed);
            RunCheck("Projectile travel, collision and line of fire", RecoilBattleCombatValidation.ValidateProjectileHandling, passed, failed);
            RunCheck("Combat ECS lifecycle and castle approach", RecoilBattleCombatValidation.ValidateEcsLifecycleAndCastle, passed, failed);
            RunCheck("Combat grid and snapshot optimizations", RecoilBattleCombatValidation.ValidateGridAndSnapshotOptimizations, passed, failed);
            RunCheck("Combat scene, roster and advance integration", RecoilUnitSimulationChecks.ValidateCombatIntegration, passed, failed);
            RunCheck("Combat tick scheduling", RecoilCombatSchedulingChecks.Run, passed, failed);
            RunCheck("Formation spacing", RecoilBattleFormationChecks.Run, passed, failed);
            RunCheck("Shared path and corridor reuse", RecoilSharedPathChecks.Run, passed, failed);
            RunCheck("Native path buffer cleanup and reuse", RecoilUnitSimulationChecks.ValidateNativeCleanup, passed, failed);
            RunCheck("Character animation correctness and wrapper cost", RecoilAnimationPerformanceChecks.Validate, passed, failed);
            RunCheck("Benchmark comparison verdicts", RecoilBenchmarkComparison.ValidateVerdicts, passed, failed);

            var summary = $"Recoil conversion evaluation: {passed.Count} passed, {failed.Count} failed.";
            if (failed.Count > 0)
                UnityEngine.Debug.LogError(summary + "\nFailed: " + string.Join("; ", failed));
            else
                UnityEngine.Debug.Log(summary + "\nPassed: " + string.Join("; ", passed));

            UnityEngine.Debug.Log("Scope: these Edit Mode checks validate deterministic behavior, resource cleanup and selected hot-path costs. They do not establish full-scene frame-time improvement or live battle correctness. In Play Mode, press F9 in the Recoil battle profiler to capture a timed CSV under Application.persistentDataPath/RecoilConversionBenchmarks; repeat with the same scene path, platform, resolution, quality, target FPS/vsync, unit count and battle duration before/after each conversion, then use Compare Benchmarks to inspect timing/workload changes and compare surviving unit counts and both castle HP values.");
        }

        private static bool AreRecoilAssembliesCurrent()
        {
            var assetsPath = Application.dataPath;
            var checks = new[]
            {
                (Assembly: typeof(RecoilUnitSimulation).Assembly, SourceDirectory: "Scripts/RecoilPathfinding"),
                (Assembly: typeof(RecoilConversionEvaluation).Assembly, SourceDirectory: "Editor/RecoilPathfinding")
            };
            var stale = new List<string>();
            foreach (var check in checks)
            {
                var assemblyPath = check.Assembly.Location;
                if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
                {
                    stale.Add($"Cannot locate compiled assembly for {check.SourceDirectory}.");
                    continue;
                }

                var sourceDirectory = Path.Combine(assetsPath, check.SourceDirectory);
                if (!Directory.Exists(sourceDirectory))
                {
                    stale.Add($"Source directory not found: {sourceDirectory}");
                    continue;
                }

                var assemblyTime = File.GetLastWriteTimeUtc(assemblyPath);
                var sourceFiles = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories);
                for (var i = 0; i < sourceFiles.Length; i++)
                {
                    if (File.GetLastWriteTimeUtc(sourceFiles[i]) <= assemblyTime) continue;
                    stale.Add($"{Path.GetRelativePath(assetsPath, sourceFiles[i])} is newer than {Path.GetFileName(assemblyPath)}.");
                }
            }

            if (stale.Count == 0)
                return true;

            UnityEngine.Debug.LogError("Recoil conversion evaluation stopped because Unity assemblies are older than source files. Wait for script compilation, then run the evaluation again.\n" +
                string.Join("\n", stale));
            if (!EditorApplication.isPlaying)
            {
                // A stale check should help recover a project whose automatic refresh was
                // skipped while the Editor was busy. Refresh on the next editor update so the
                // current menu invocation can return before compilation/domain reload begins.
                EditorApplication.delayCall += () =>
                {
                    if (!EditorApplication.isPlaying)
                        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                };
                UnityEngine.Debug.Log("Recoil conversion evaluation requested an Edit Mode asset refresh. Wait for Unity compilation, then run the evaluation again.");
            }
            return false;
        }

        private static void RunCheck(string name, Action action, List<string> passed, List<string> failed)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                action();
                timer.Stop();
                passed.Add($"{name} ({timer.ElapsedMilliseconds} ms)");
            }
            catch (Exception exception)
            {
                timer.Stop();
                failed.Add($"{name} ({timer.ElapsedMilliseconds} ms): {exception.GetBaseException().Message}");
                UnityEngine.Debug.LogException(exception);
            }
        }
    }
}
