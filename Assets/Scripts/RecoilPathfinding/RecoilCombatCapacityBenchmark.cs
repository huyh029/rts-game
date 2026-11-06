using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        public static bool CapacityBenchmarkHidesUi { get; private set; }
        public bool CapacityBenchmarkRunning { get; private set; }
        public string CapacityBenchmarkReport { get; private set; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCapacityBenchmarkUi() => CapacityBenchmarkHidesUi = false;

        public void StartCombatCapacityBenchmark()
        {
            if (CapacityBenchmarkRunning) return;
            CapacityBenchmarkRunning = true;
            StartCoroutine(MeasureCombatCapacity());
        }

        private (int blue, int red, int inRange) CapacityPopulation()
        {
            CompletePendingCombatJob();
            var blue = 0; var red = 0; var inRange = 0;
            foreach (var soldier in _blueTeam) if (soldier != null && !soldier.Retired) blue++;
            foreach (var soldier in _redTeam) if (soldier != null && !soldier.Retired) red++;
            for (var i = 0; i < _combatSoldiers.Count; i++)
            {
                var unit = _combatSnapshots[i];
                var target = unit.CurrentTargetIndex;
                if (unit.Active == 0 || unit.Health <= 0 || target < 0 || target >= _combatSoldiers.Count) continue;
                var other = _combatSnapshots[target];
                if (other.Active == 0 || other.Health <= 0 || unit.Team == other.Team) continue;
                var range = unit.Stats.IsConfigured ? unit.Stats.Range : attackRange;
                if (Unity.Mathematics.math.distancesq(unit.Position.xz, other.Position.xz) <= (range + 1f) * (range + 1f)) inRange++;
            }
            return (blue, red, inRange);
        }

        private void RefillCapacityArmy(int targetPerTeam, int blue, int red)
        {
            var blueMissing = Mathf.Max(0, targetPerTeam - blue - _blueSelectedSpawnTotal - _blueSpawnQueue);
            var redMissing = Mathf.Max(0, targetPerTeam - red - _redSelectedSpawnTotal - _redSpawnQueue);
            for (var type = 0; type < CharacterTypeCount; type++)
            {
                var blueAmount = blueMissing / CharacterTypeCount + (type < blueMissing % CharacterTypeCount ? 1 : 0);
                var redAmount = redMissing / CharacterTypeCount + (type < redMissing % CharacterTypeCount ? 1 : 0);
                if (blueAmount > 0) { EnqueueSpawnType(_blueSelectedSpawnQueue, type, blueAmount); _blueSelectedSpawnTotal += blueAmount; }
                if (redAmount > 0) { EnqueueSpawnType(_redSelectedSpawnQueue, type, redAmount); _redSelectedSpawnTotal += redAmount; }
            }
            _lobbyStarting = true;
        }

        private IEnumerator MeasureCombatCapacity()
        {
            var disabled = new List<Behaviour>();
            var oldVsync = QualitySettings.vSyncCount;
            var oldFrameRate = Application.targetFrameRate;
            var oldBackground = Application.runInBackground;
            var samples = new List<float>(8192);
            var report = new System.Text.StringBuilder();
            var directory = Path.Combine(Application.dataPath, "CombatCapacityReports");
            Directory.CreateDirectory(directory);
            var stem = Path.Combine(directory, "capacity-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            CapacityBenchmarkReport = stem + ".txt";
            StreamWriter csv = null;
            try
            {
                CapacityBenchmarkHidesUi = true;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                Application.runInBackground = true;
                foreach (var behaviour in FindObjectsByType<Behaviour>(FindObjectsSortMode.None))
                {
                    if (!behaviour.enabled) continue;
                    if (behaviour is Canvas || behaviour is UnityEngine.UIElements.UIDocument ||
                        behaviour is RecoilBattleMinimap || behaviour is RecoilBattlePerformanceMonitor ||
                        behaviour is UnityEngine.EventSystems.EventSystem || behaviour.GetType().Name == "AppUIManagerBehaviour")
                    { disabled.Add(behaviour); behaviour.enabled = false; }
                }
                report.AppendLine("Combat capacity, UI disabled; rendering, animation, movement, combat and reinforcement spawn included.");
                report.AppendLine($"Scene={gameObject.scene.path}; platform={Application.platform}; resolution={Screen.width}x{Screen.height}; quality={QualitySettings.GetQualityLevel()}");
                report.AppendLine($"CPU={SystemInfo.processorType}; GPU={SystemInfo.graphicsDeviceName}; RAM={SystemInfo.systemMemorySize} MB; VRAM={SystemInfo.graphicsMemorySize} MB");
                report.AppendLine("Each tier: up to 120s buildup, 5s warmup, 20s capture. FPS caps disabled. Threshold uses p95 frame time and simulation drop.");
                csv = new StreamWriter(stem + ".csv");
                csv.WriteLine("target_total,elapsed_s,frame_ms,blue_alive,red_alive,in_weapon_range,dropped_sim_ms,ticks");
                var deadline = Time.realtimeSinceStartupAsDouble + 150;
                while (!_lobbyStarted && Time.realtimeSinceStartupAsDouble < deadline)
                { StartMatchFromLobby(); yield return null; }
                if (!_lobbyStarted) { report.AppendLine("BLOCKED: match could not start (validation or pool prewarm)."); yield break; }
                if (_matchFinished) { report.AppendLine("BLOCKED: current match already finished; start a fresh Play session."); yield break; }
                var best30 = 0; var best60 = 0;
                foreach (var targetTotal in new[] { 500, 1000, 2000, 3000, 4000, 6000, 8000 })
                {
                    var target = targetTotal / 2;
                    var population = CapacityPopulation();
                    if (population.blue + population.red > targetTotal * 1.1f)
                    { report.AppendLine($"SKIP {targetTotal}: existing army exceeds tier."); continue; }
                    deadline = Time.realtimeSinceStartupAsDouble + 120;
                    var nextRefill = 0d;
                    while (!_matchFinished && Time.realtimeSinceStartupAsDouble < deadline)
                    {
                        if (Time.realtimeSinceStartupAsDouble >= nextRefill)
                        {
                            population = CapacityPopulation();
                            RefillCapacityArmy(target, population.blue, population.red);
                            nextRefill = Time.realtimeSinceStartupAsDouble + 1;
                            if (_combatStarted && population.blue >= target * .9f && population.red >= target * .9f && population.inRange >= 8) break;
                        }
                        yield return null;
                    }
                    if (_matchFinished || !_combatStarted || population.blue < target * .9f || population.red < target * .9f || population.inRange < 8)
                    { report.AppendLine($"INCONCLUSIVE {targetTotal}: spawn/combat threshold not reached. Actual blue={population.blue}, red={population.red}, in-range={population.inRange}."); break; }
                    var start = Time.realtimeSinceStartupAsDouble;
                    var droppedStart = RecoilSimulationClock.DroppedSeconds;
                    var lastDropped = droppedStart;
                    var sum = 0d; var aliveSum = 0d; var minimumAlive = int.MaxValue; var maximumAlive = 0; var validFrames = 0;
                    samples.Clear();
                    while (!_matchFinished && Time.realtimeSinceStartupAsDouble - start < 25)
                    {
                        yield return null;
                        var now = Time.realtimeSinceStartupAsDouble;
                        if (now >= nextRefill)
                        {
                            population = CapacityPopulation();
                            RefillCapacityArmy(target, population.blue, population.red);
                            nextRefill = now + 1;
                        }
                        if (now - start < 5) { droppedStart = lastDropped = RecoilSimulationClock.DroppedSeconds; continue; }
                        var ms = Time.unscaledDeltaTime * 1000f;
                        var count = population.blue + population.red;
                        samples.Add(ms); sum += ms; aliveSum += count;
                        minimumAlive = Mathf.Min(minimumAlive, count); maximumAlive = Mathf.Max(maximumAlive, count);
                        if (population.blue >= target * .9f && population.red >= target * .9f && population.inRange >= 8) validFrames++;
                        var dropped = RecoilSimulationClock.DroppedSeconds;
                        csv.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:F3},{2:F3},{3},{4},{5},{6:F3},{7}",
                            targetTotal, now - start - 5, ms, population.blue, population.red, population.inRange, (dropped - lastDropped) * 1000, RecoilSimulationClock.LastTicksThisFrame));
                        lastDropped = dropped;
                    }
                    if (samples.Count == 0) { report.AppendLine("INCONCLUSIVE: no captured frames."); break; }
                    samples.Sort();
                    var p95 = samples[Mathf.Clamp(Mathf.CeilToInt(samples.Count * .95f) - 1, 0, samples.Count - 1)];
                    var droppedSeconds = RecoilSimulationClock.DroppedSeconds - droppedStart;
                    var valid = !_matchFinished && validFrames >= samples.Count * .9f;
                    var pass30 = valid && p95 <= 1000f / 30 && droppedSeconds < .2;
                    var pass60 = valid && p95 <= 1000f / 60 && droppedSeconds < .2;
                    if (pass30) best30 = targetTotal;
                    if (pass60) best60 = targetTotal;
                    report.AppendLine(FormattableString.Invariant($"Tier {targetTotal}: alive min/avg/max={minimumAlive}/{aliveSum / samples.Count:F0}/{maximumAlive}, avg FPS={samples.Count * 1000 / sum:F1}, frame p95={p95:F2}ms, sim dropped={droppedSeconds:F3}s, valid={valid}, 30FPS={pass30}, 60FPS={pass60}"));
                    csv.Flush();
                    File.WriteAllText(CapacityBenchmarkReport, report.ToString());
                    if (!valid || !pass30) break;
                }
                report.AppendLine($"Highest validated tested tier: 30FPS={best30}, 60FPS={best60}. Zero means no tier established; not a zero-unit capacity. Not a theoretical maximum.");
            }
            finally
            {
                csv?.Dispose();
                File.WriteAllText(CapacityBenchmarkReport, report.ToString());
                QualitySettings.vSyncCount = oldVsync;
                Application.targetFrameRate = oldFrameRate;
                Application.runInBackground = oldBackground;
                foreach (var behaviour in disabled) if (behaviour != null) behaviour.enabled = true;
                CapacityBenchmarkHidesUi = false;
                CapacityBenchmarkRunning = false;
                Debug.Log("Combat capacity report: " + CapacityBenchmarkReport + "\n" + report);
            }
        }
    }
}
