using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RecoilPathfindingEditor
{
    public sealed class RecoilBenchmarkComparison : EditorWindow
    {
        private sealed class Run
        {
            public string Path;
            public string Scene;
            public string ScenePath;
            public string Platform;
            public int Width;
            public int Height;
            public int Quality;
            public int TargetFrameRate;
            public int VSyncCount;
            public int Rows;
            public int BlueStart;
            public int RedStart;
            public int BlueEnd;
            public int RedEnd;
            public int BlueCastleStart;
            public int RedCastleStart;
            public int BlueCastleEnd;
            public int RedCastleEnd;
            public double ElapsedStart;
            public double ElapsedEnd;
            public readonly Dictionary<string, List<double>> Metrics = new();
        }

        private string _beforePath = string.Empty;
        private string _afterPath = string.Empty;
        private string _report = "Select a before and after CSV captured with F9 in the battle profiler.";
        private Vector2 _scroll;

        [MenuItem("Tools/Recoil Pathfinding/Compare Benchmarks")]
        private static void Open()
        {
            GetWindow<RecoilBenchmarkComparison>("Recoil Benchmark Compare");
        }

        public static void ValidateVerdicts()
        {
            var before = CreateValidationRun();
            var after = CreateValidationRun();
            before.Metrics["frame_ms"] = new List<double> { 20d, 20d, 20d, 20d };
            after.Metrics["frame_ms"] = new List<double> { 18d, 18d, 18d, 18d };
            before.Metrics["cpu_frame_ms"] = new List<double> { 12d, 12d, 12d, 12d };
            after.Metrics["cpu_frame_ms"] = new List<double> { 10d, 10d, 10d, 10d };
            before.Metrics["movement_ms"] = new List<double> { 4d, 4d, 4d, 4d };
            after.Metrics["movement_ms"] = new List<double> { 3d, 3d, 3d, 3d };
            before.Metrics["target_search_requests"] = new List<double> { 100d, 100d };
            before.Metrics["target_searches_performed"] = new List<double> { 75d, 75d };
            before.Metrics["target_searches_deferred"] = new List<double> { 25d, 25d };
            after.Metrics["target_search_requests"] = new List<double> { 100d, 100d };
            after.Metrics["target_searches_performed"] = new List<double> { 100d, 100d };
            after.Metrics["target_searches_deferred"] = new List<double> { 0d, 0d };
            before.Metrics["battle_ms"] = new List<double> { 5d, 5d, 5d, 5d };
            after.Metrics["battle_ms"] = new List<double> { 4d, 4d, 4d, 4d };
            before.Metrics["presentation_ms"] = new List<double> { 8d, 8d, 8d, 8d };
            after.Metrics["presentation_ms"] = new List<double> { 6d, 6d, 6d, 6d };
            var report = BuildReport(before, after);
            Require(report.Contains("Rendered frame p95: IMPROVED") &&
                report.Contains("Movement phase p95: IMPROVED") &&
                report.Contains("Presentation tier update p95: IMPROVED") &&
                report.Contains("final surviving unit counts and both castle HP values match") &&
                report.Contains("Before target searches: requested 100.0/frame, performed 75.0/frame, deferred 25.0/frame") &&
                report.Contains("After target searches: requested 100.0/frame, performed 100.0/frame, deferred 0.0/frame"),
                "Matched runs with lower p95 costs and unchanged outcomes must report improvement and matching behavior signals.");

            after.Width++;
            report = BuildReport(before, after);
            Require(report.Contains("SCENARIO MISMATCH") &&
                report.Contains("Performance verdict: INCONCLUSIVE"),
                "Performance verdicts must be suppressed when captured scenarios differ.");

            after.Width = before.Width;
            after.BlueEnd++;
            report = BuildReport(before, after);
            Require(report.Contains("final surviving unit counts or castle HP changed"),
                "A changed battle outcome must be flagged for gameplay review.");

            before.Metrics["movement_ms"] = new List<double> { 0d, 0d, 0d, 0d };
            after.Metrics["movement_ms"] = new List<double> { 1d, 1d, 1d, 1d };
            report = BuildReport(before, after);
            Require(report.Contains("Movement phase p95: REGRESSED") && report.Contains("new cost"),
                "A cost appearing from a zero-cost baseline must be reported as a regression.");
        }

        private static Run CreateValidationRun() => new()
        {
            Path = "validation.csv",
            Scene = "ValidationScene",
            ScenePath = "Assets/ValidationScene.unity",
            Platform = "WindowsEditor",
            Width = 1920,
            Height = 1080,
            Quality = 2,
            TargetFrameRate = 60,
            VSyncCount = 0,
            Rows = 4,
            BlueStart = 100,
            RedStart = 100,
            BlueEnd = 80,
            RedEnd = 80,
            BlueCastleStart = 100000,
            RedCastleStart = 100000,
            BlueCastleEnd = 95000,
            RedCastleEnd = 95000,
            ElapsedStart = 0d,
            ElapsedEnd = 60d
        };

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Compare fixed-duration Play Mode captures", EditorStyles.boldLabel);
            _beforePath = DrawFileRow("Before", _beforePath);
            _afterPath = DrawFileRow("After", _afterPath);
            EditorGUILayout.HelpBox("For a useful comparison, capture the same scene path, platform, resolution, quality, target FPS/vsync, starting army sizes and castle HP. CSV files are written under Application.persistentDataPath/RecoilConversionBenchmarks.", MessageType.Info);
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_beforePath) || string.IsNullOrEmpty(_afterPath)))
            {
                if (GUILayout.Button("Compare runs"))
                    Compare();
            }
            EditorGUILayout.Space();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.TextArea(_report, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private static string DrawFileRow(string label, string path)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                path = EditorGUILayout.TextField(path);
                if (GUILayout.Button("Browse", GUILayout.Width(70f)))
                    path = EditorUtility.OpenFilePanel($"Select {label} benchmark CSV", Path.GetDirectoryName(path), "csv");
            }
            return path;
        }

        private void Compare()
        {
            try
            {
                var before = ReadRun(_beforePath);
                var after = ReadRun(_afterPath);
                _report = BuildReport(before, after);
            }
            catch (Exception exception)
            {
                _report = "Could not compare benchmark files: " + exception.Message;
            }
        }

        private static Run ReadRun(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Benchmark CSV not found", path);
            var lines = File.ReadAllLines(path);
            if (lines.Length < 2) throw new InvalidDataException("The CSV has no frame samples.");
            var headers = ParseCsvLine(lines[0]);
            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++) columns[headers[i]] = i;
            foreach (var required in new[] { "scene", "scene_path", "platform", "screen_width", "screen_height", "quality_level", "target_frame_rate", "vsync_count", "elapsed_s", "blue_units", "red_units", "blue_castle_hp", "red_castle_hp", "cpu_timing_valid", "gpu_timing_valid" })
                if (!columns.ContainsKey(required)) throw new InvalidDataException($"Missing column '{required}'. This file may be from an older benchmark build.");

            var run = new Run { Path = path };
            var metrics = new[] { "frame_ms", "cpu_frame_ms", "gpu_frame_ms", "ticks_this_frame", "dropped_simulation_ms", "movement_ms", "battle_ms", "presentation_ms", "movement_prepare_ms", "movement_wait_ms", "movement_wait_grid_ms", "movement_wait_simulation_ms", "movement_wait_collect_ms", "movement_apply_ms", "movement_fallback_ms", "combat_lifecycle_ms", "combat_departures_ms", "combat_schedule_ms", "combat_snapshot_grid_ms", "combat_capture_ecs_ms", "combat_grid_update_ms", "combat_close_targeting_ms", "combat_refresh_ecs_ms", "combat_resolve_ms", "combat_retire_ms", "combat_roster_build_ms", "combat_alive_capture_ms", "combat_roster_compact_ms", "combat_projectile_resolve_ms", "combat_candidates", "close_candidates", "los_checks", "los_blocked", "strategic_los_checks", "strategic_los_blocked", "target_weight_evaluations", "target_priority_skips", "close_weight_evaluations", "close_priority_skips", "projectile_candidates", "projectile_impacts", "lead_solutions", "lead_fallbacks", "avoided_category_candidates", "avoided_category_selection_events", "path_completions", "path_cancellations", "memory_allocated_mb", "target_search_requests", "target_searches_performed", "target_searches_deferred" };
            foreach (var metric in metrics)
                if (columns.ContainsKey(metric)) run.Metrics[metric] = new List<double>();

            for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
            {
                if (string.IsNullOrWhiteSpace(lines[lineIndex])) continue;
                var row = ParseCsvLine(lines[lineIndex]);
                string Cell(string key) => row[columns[key]];
                int IntCell(string key) => int.Parse(Cell(key), CultureInfo.InvariantCulture);
                var elapsed = double.Parse(Cell("elapsed_s"), CultureInfo.InvariantCulture);
                if (run.Rows == 0)
                {
                    run.Scene = Cell("scene"); run.ScenePath = Cell("scene_path"); run.Platform = Cell("platform");
                    run.Width = IntCell("screen_width"); run.Height = IntCell("screen_height");
                    run.Quality = IntCell("quality_level");
                    run.TargetFrameRate = IntCell("target_frame_rate"); run.VSyncCount = IntCell("vsync_count");
                    run.BlueStart = IntCell("blue_units"); run.RedStart = IntCell("red_units");
                    run.BlueCastleStart = IntCell("blue_castle_hp"); run.RedCastleStart = IntCell("red_castle_hp");
                    run.ElapsedStart = elapsed;
                }
                run.ElapsedEnd = elapsed;
                run.BlueEnd = IntCell("blue_units"); run.RedEnd = IntCell("red_units");
                run.BlueCastleEnd = IntCell("blue_castle_hp"); run.RedCastleEnd = IntCell("red_castle_hp");
                foreach (var metric in run.Metrics.Keys)
                    if ((metric != "cpu_frame_ms" || IntCell("cpu_timing_valid") != 0) &&
                        (metric != "gpu_frame_ms" || IntCell("gpu_timing_valid") != 0) &&
                        double.TryParse(Cell(metric), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        run.Metrics[metric].Add(value);
                run.Rows++;
            }
            if (run.Rows == 0) throw new InvalidDataException("The CSV has no valid frame rows.");
            return run;
        }

        private static string BuildReport(Run before, Run after)
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine($"Before: {before.Path} ({before.Rows} frames)");
            report.AppendLine($"After:  {after.Path} ({after.Rows} frames)");
            var mismatches = new List<string>();
            if (before.Scene != after.Scene) mismatches.Add($"scene {before.Scene} != {after.Scene}");
            if (before.ScenePath != after.ScenePath) mismatches.Add($"scene path {before.ScenePath} != {after.ScenePath}");
            if (before.Platform != after.Platform) mismatches.Add($"platform {before.Platform} != {after.Platform}");
            if (before.Width != after.Width || before.Height != after.Height) mismatches.Add($"resolution {before.Width}x{before.Height} != {after.Width}x{after.Height}");
            if (before.Quality != after.Quality) mismatches.Add($"quality level {before.Quality} != {after.Quality}");
            if (before.TargetFrameRate != after.TargetFrameRate || before.VSyncCount != after.VSyncCount) mismatches.Add($"target FPS/vsync {before.TargetFrameRate}/{before.VSyncCount} != {after.TargetFrameRate}/{after.VSyncCount}");
            if (before.BlueStart != after.BlueStart || before.RedStart != after.RedStart) mismatches.Add($"starting units blue/red {before.BlueStart}/{before.RedStart} != {after.BlueStart}/{after.RedStart}");
            if (before.BlueCastleStart != after.BlueCastleStart || before.RedCastleStart != after.RedCastleStart) mismatches.Add("starting castle HP differs");
            if (Math.Abs((before.ElapsedEnd - before.ElapsedStart) - (after.ElapsedEnd - after.ElapsedStart)) > 1d) mismatches.Add("capture durations differ by more than 1s");
            report.AppendLine(mismatches.Count == 0
                ? "Scenario checks: matched for the metadata available in the CSV."
                : "SCENARIO MISMATCH: " + string.Join("; ", mismatches));
            report.AppendLine("Metric                           Before avg / p95 / max       After avg / p95 / max       Change in avg");
            var unavailableMetrics = new List<string>();
            foreach (var metric in new[] { "frame_ms", "cpu_frame_ms", "gpu_frame_ms", "ticks_this_frame", "dropped_simulation_ms", "movement_ms", "battle_ms", "presentation_ms", "movement_prepare_ms", "movement_wait_ms", "movement_wait_grid_ms", "movement_wait_simulation_ms", "movement_wait_collect_ms", "movement_apply_ms", "movement_fallback_ms", "combat_lifecycle_ms", "combat_departures_ms", "combat_schedule_ms", "combat_snapshot_grid_ms", "combat_capture_ecs_ms", "combat_grid_update_ms", "combat_close_targeting_ms", "combat_refresh_ecs_ms", "combat_resolve_ms", "combat_retire_ms", "combat_roster_build_ms", "combat_alive_capture_ms", "combat_roster_compact_ms", "combat_projectile_resolve_ms", "combat_candidates", "close_candidates", "los_checks", "los_blocked", "strategic_los_checks", "strategic_los_blocked", "target_weight_evaluations", "target_priority_skips", "close_weight_evaluations", "close_priority_skips", "projectile_candidates", "projectile_impacts", "lead_solutions", "lead_fallbacks", "avoided_category_candidates", "avoided_category_selection_events", "path_completions", "path_cancellations", "memory_allocated_mb", "target_search_requests", "target_searches_performed", "target_searches_deferred" })
            {
                if (!before.Metrics.TryGetValue(metric, out var oldValues) || !after.Metrics.TryGetValue(metric, out var newValues) || oldValues.Count == 0 || newValues.Count == 0)
                {
                    unavailableMetrics.Add(metric);
                    continue;
                }
                var oldStats = Stats(oldValues); var newStats = Stats(newValues);
                var change = oldStats.Average != 0d ? (newStats.Average - oldStats.Average) / oldStats.Average * 100d : 0d;
                report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-32} {1,7:F2} / {2,7:F2} / {3,7:F2}   {4,7:F2} / {5,7:F2} / {6,7:F2}   {7:+0.0;-0.0;0.0}%",
                    metric, oldStats.Average, oldStats.P95, oldStats.Max, newStats.Average, newStats.P95, newStats.Max, change));
            }
            if (unavailableMetrics.Count > 0)
                report.AppendLine("Metrics omitted (missing columns or valid samples in at least one CSV): " +
                    string.Join(", ", unavailableMetrics));
            if (before.Metrics.TryGetValue("dropped_simulation_ms", out var oldDropped) && oldDropped.Count > 0 &&
                after.Metrics.TryGetValue("dropped_simulation_ms", out var newDropped) && newDropped.Count > 0)
            {
                var oldDroppedTotal = Sum(oldDropped);
                var newDroppedTotal = Sum(newDropped);
                report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "Fixed-step time dropped: before {0:F3}s, after {1:F3}s.",
                    oldDroppedTotal / 1000d, newDroppedTotal / 1000d));
            }
            report.AppendLine($"Battle result: blue units {before.BlueStart}->{before.BlueEnd} vs {after.BlueStart}->{after.BlueEnd}; red units {before.RedStart}->{before.RedEnd} vs {after.RedStart}->{after.RedEnd}.");
            report.AppendLine($"Castle HP change: blue {before.BlueCastleStart}->{before.BlueCastleEnd} vs {after.BlueCastleStart}->{after.BlueCastleEnd}; red {before.RedCastleStart}->{before.RedCastleEnd} vs {after.RedCastleStart}->{after.RedCastleEnd}.");
            report.AppendLine();
            report.AppendLine("Evaluation:");
            if (mismatches.Count > 0)
            {
                report.AppendLine("Performance verdict: INCONCLUSIVE. Scenario metadata differs; recapture both runs with matching conditions.");
            }
            else
            {
                AppendP95Verdict(report, "Rendered frame", "frame_ms", before, after);
                AppendP95Verdict(report, "CPU frame", "cpu_frame_ms", before, after);
                AppendP95Verdict(report, "Movement phase", "movement_ms", before, after);
                AppendP95Verdict(report, "Battle phase", "battle_ms", before, after);
                AppendP95Verdict(report, "Presentation tier update", "presentation_ms", before, after);
                foreach (var phase in new[]
                {
                    ("Movement prepare", "movement_prepare_ms"),
                    ("Movement wait", "movement_wait_ms"),
                    ("Movement grid wait", "movement_wait_grid_ms"),
                    ("Movement simulation wait", "movement_wait_simulation_ms"),
                    ("Movement collect wait", "movement_wait_collect_ms"),
                    ("Movement apply", "movement_apply_ms"),
                    ("Movement fallback", "movement_fallback_ms"),
                    ("Combat lifecycle", "combat_lifecycle_ms"),
                    ("Combat departures", "combat_departures_ms"),
                    ("Combat scheduling", "combat_schedule_ms"),
                    ("Combat snapshot and grid", "combat_snapshot_grid_ms"),
                    ("Combat ECS capture", "combat_capture_ecs_ms"),
                    ("Combat grid update", "combat_grid_update_ms"),
                    ("Combat close targeting", "combat_close_targeting_ms"),
                    ("Combat ECS refresh", "combat_refresh_ecs_ms"),
                    ("Combat resolve", "combat_resolve_ms"),
                    ("Combat retirement", "combat_retire_ms"),
                    ("Combat roster build", "combat_roster_build_ms"),
                    ("Combat alive capture", "combat_alive_capture_ms"),
                    ("Combat roster compact", "combat_roster_compact_ms"),
                    ("Projectile resolve", "combat_projectile_resolve_ms")
                })
                    AppendP95Verdict(report, phase.Item1, phase.Item2, before, after);
            }

            var outcomesMatch = before.BlueEnd == after.BlueEnd && before.RedEnd == after.RedEnd &&
                before.BlueCastleEnd == after.BlueCastleEnd && before.RedCastleEnd == after.RedCastleEnd;
            report.AppendLine(outcomesMatch
                ? "Behavior signal: final surviving unit counts and both castle HP values match."
                : "Behavior signal: final surviving unit counts or castle HP changed; inspect this as a gameplay difference before accepting the conversion.");
            report.AppendLine("This outcome signal checks only final counts and castle HP; it does not prove identical movement, target choices, projectile paths, or attack timing.");
            AppendTargetSearchSignal(report, "Before", before);
            AppendTargetSearchSignal(report, "After", after);
            return report.ToString();
        }

        private static void AppendTargetSearchSignal(System.Text.StringBuilder report, string label, Run run)
        {
            if (!run.Metrics.TryGetValue("target_search_requests", out var requests) || requests.Count == 0 ||
                !run.Metrics.TryGetValue("target_searches_performed", out var performed) || performed.Count == 0 ||
                !run.Metrics.TryGetValue("target_searches_deferred", out var deferred) || deferred.Count == 0)
            {
                report.AppendLine($"{label} target searches: unavailable (capture lacks target-search counters).");
                return;
            }

            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0} target searches: requested {1:F1}/frame, performed {2:F1}/frame, deferred {3:F1}/frame.",
                label, Stats(requests).Average, Stats(performed).Average, Stats(deferred).Average));
        }

        private static void AppendP95Verdict(System.Text.StringBuilder report, string label, string metric,
            Run before, Run after)
        {
            if (!before.Metrics.TryGetValue(metric, out var beforeSamples) || beforeSamples.Count == 0 ||
                !after.Metrics.TryGetValue(metric, out var afterSamples) || afterSamples.Count == 0)
            {
                report.AppendLine($"{label} p95: unavailable (no valid samples in one or both captures).");
                return;
            }

            var beforeP95 = Stats(beforeSamples).P95;
            var afterP95 = Stats(afterSamples).P95;
            var hasBaseline = beforeP95 > 0d;
            var change = hasBaseline ? (afterP95 - beforeP95) / beforeP95 * 100d : 0d;
            var verdict = !hasBaseline
                ? afterP95 > 0d ? "REGRESSED" : "NO MATERIAL CHANGE"
                : change < -5d ? "IMPROVED" : change > 5d ? "REGRESSED" : "NO MATERIAL CHANGE";
            var changeText = hasBaseline
                ? change.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%"
                : afterP95 > 0d ? "new cost" : "0.0%";
            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0} p95: {1} ({2:F2} -> {3:F2} ms, {4}). Threshold: 5%.",
                label, verdict, beforeP95, afterP95, changeText));
        }

        private static (double Average, double P95, double Max) Stats(List<double> values)
        {
            var sum = 0d;
            var max = double.MinValue;
            for (var i = 0; i < values.Count; i++) { sum += values[i]; max = Math.Max(max, values[i]); }
            values.Sort();
            var p95Index = Math.Max(0, (int)Math.Ceiling(values.Count * 0.95d) - 1);
            return (sum / values.Count, values[p95Index], max);
        }

        private static double Sum(List<double> values)
        {
            var sum = 0d;
            for (var i = 0; i < values.Count; i++) sum += values[i];
            return sum;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var value = new System.Text.StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var character = line[i];
                if (character == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else if (character == ',' && !quoted) { fields.Add(value.ToString()); value.Clear(); }
                else value.Append(character);
            }
            fields.Add(value.ToString());
            return fields;
        }
    }
}
