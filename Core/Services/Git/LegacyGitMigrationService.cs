using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Serialization;
using AIShikikan.Core.Services.Git;

namespace AIShikikan.Core.Services.Git;

/// <summary>
/// 旧 GitStep 无感迁移服务。
/// 扫描旧 steps/*.json 记录，通过 GitService 解析上下文并创建新 Checkpoint，
/// 建立 oldStepId → newCheckpointId 映射，更新 assignments/sessions 中的旧 stepId。
/// </summary>
public sealed class LegacyGitMigrationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly GitStepService _git;
    private readonly string _checkpointsDir;
    private readonly object _lock = new();

    /// <summary>旧步骤 → 新检查点 ID 的映射(内存缓存)。</summary>
    private readonly Dictionary<string, string> _stepToCheckpointMap = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>迁移状态文件路径(防重复迁移)。</summary>
    private static readonly string MigrationStatePath =
        Path.Combine(AppPaths.DataDir, "legacy-migration-state.json");

    /// <summary>
    /// 旧步骤记录的完整快照(用于反序列化旧 JSON 文件)。
    /// 与当前 GitStepRecord 解耦，作为遗留数据 DTO 独立存在。
    /// </summary>
    public sealed class LegacyGitStepRecord
    {
        public string StepId { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string BaseBranch { get; set; } = string.Empty;
        public string StepBranch { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string RollbackMode { get; set; } = "drop";
        public string? MergeCommit { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? CompletedAt { get; set; }
    }

    /// <summary>
    /// 新检查点记录(与旧 steps 并列存储，不覆盖旧源文件)。
    /// </summary>
    public sealed class GitCheckpointRecord
    {
        public string CheckpointId { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Label { get; set; } = string.Empty;
        public string SourceStepId { get; set; } = string.Empty;
        public string BaseBranch { get; set; } = string.Empty;
        public string CommitHash { get; set; } = string.Empty;
        public string? MergeCommit { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? CompletedAt { get; set; }
        public bool Merged { get; set; }
    }

    /// <summary>
    /// 迁移汇总结果(结构化, 供 UI 弹窗或重试入口使用)。
    /// </summary>
    public sealed class MigrationSummary
    {
        public int TotalOldSteps { get; set; }
        public int MigratedSuccessfully { get; set; }
        public int MigratedWithWarnings { get; set; }
        public int FailedToMigrate { get; set; }
        public int SkippedAlreadyMigrated { get; set; }
        public List<MigrationItem> Items { get; set; } = new();
        public bool CanRetry { get; set; }
        public string? SummaryMessage { get; set; }
    }

    /// <summary>
    /// 单条迁移结果。
    /// </summary>
    public sealed class MigrationItem
    {
        public string OldStepId { get; set; } = string.Empty;
        public string? NewCheckpointId { get; set; }
        public string Status { get; set; } = "pending";
        public string? Reason { get; set; }
        public string? ContextDetail { get; set; }
    }

    /// <summary>
    /// 迁移持久化状态(防重复)。
    /// </summary>
    public sealed class MigrationState
    {
        public bool HasRun { get; set; }
        public DateTime? LastRunAt { get; set; }
        public List<string> MigratedStepIds { get; set; } = new();
        public Dictionary<string, string> StepToCheckpointMap { get; set; } = new();
        public string? LastSummaryJson { get; set; }
    }

    public LegacyGitMigrationService(GitStepService git)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _checkpointsDir = Path.Combine(AppPaths.DataDir, "checkpoints");
    }

    /// <summary>
    /// 执行迁移: 扫描旧 steps → 解析上下文 → 创建 Checkpoint → 更新 JSON。
    /// 部分成功不删除旧源文件；持久化迁移状态防重复。
    /// 无法迁移返回结构化汇总，不在 Core 阻塞启动。
    /// </summary>
    public MigrationSummary Migrate()
    {
        var summary = new MigrationSummary { TotalOldSteps = 0 };

        // 1. 检查是否已迁移过(幂等)
        var state = LoadState();
        if (state.HasRun && state.MigratedStepIds.Count > 0)
        {
            summary.SkippedAlreadyMigrated = state.MigratedStepIds.Count;
            summary.CanRetry = true;
            summary.SummaryMessage = $"已迁移过 {state.MigratedStepIds.Count} 条记录; 可通过重试入口处理新增步骤。";
            Log.Info("Migration", summary.SummaryMessage);
            return summary;
        }

        // 2. 扫描旧 steps 目录
        if (!Directory.Exists(AppPaths.StepsDir))
        {
            summary.SummaryMessage = "旧 steps 目录不存在, 无需迁移。";
            return summary;
        }

        var stepFiles = Directory.GetFiles(AppPaths.StepsDir, "*.json");
        summary.TotalOldSteps = stepFiles.Length;

        if (stepFiles.Length == 0)
        {
            state.HasRun = true;
            state.LastRunAt = DateTime.Now;
            SaveState(state);
            summary.SummaryMessage = "未发现旧步骤记录, 无需迁移。";
            return summary;
        }

        Log.Info("Migration", $"开始迁移: 扫描到 {stepFiles.Length} 个旧步骤文件");

        // 3. 逐条扫描、解析、映射、创建 Checkpoint
        foreach (var file in stepFiles)
        {
            var item = MigrateSingle(file, state);
            summary.Items.Add(item);

            switch (item.Status)
            {
                case "migrated":
                    summary.MigratedSuccessfully++;
                    break;
                case "migrated-with-warnings":
                    summary.MigratedSuccessfully++;
                    summary.MigratedWithWarnings++;
                    break;
                case "failed":
                    summary.FailedToMigrate++;
                    break;
                case "skipped":
                    summary.SkippedAlreadyMigrated++;
                    break;
            }
        }

        // 4. 更新 assignments/*.json 与 sessions/*.json 中的旧 stepId
        var migratedItems = summary.Items.Where(i => i.NewCheckpointId is not null).ToList();
        if (migratedItems.Count > 0)
        {
            var updateResult = UpdateJsonReferences(migratedItems);
            if (!updateResult)
            {
                Log.Warn("Migration", "部分 JSON 引用更新失败, 可重试");
                summary.CanRetry = true;
            }
        }

        // 5. 持久化迁移状态(不删除旧源文件)
        state.HasRun = true;
        state.LastRunAt = DateTime.Now;
        state.MigratedStepIds = summary.Items
            .Where(i => i.Status == "migrated" || i.Status == "migrated-with-warnings")
            .Select(i => i.OldStepId).ToList();
        state.StepToCheckpointMap = summary.Items
            .Where(i => i.NewCheckpointId is not null)
            .ToDictionary(i => i.OldStepId, i => i.NewCheckpointId!, StringComparer.OrdinalIgnoreCase);
        state.LastSummaryJson = JsonSerializer.Serialize(summary, SerializerOptions);
        SaveState(state);

        // 6. 构建汇总消息
        summary.CanRetry = summary.FailedToMigrate > 0 || summary.MigratedWithWarnings > 0;
        summary.SummaryMessage = BuildSummaryMessage(summary);

        Log.Info("Migration", $"迁移完成: 成功={summary.MigratedSuccessfully}, 失败={summary.FailedToMigrate}, 跳过={summary.SkippedAlreadyMigrated}");
        return summary;
    }

    /// <summary>
    /// 获取已迁移的 oldStepId → newCheckpointId 映射。
    /// </summary>
    public IReadOnlyDictionary<string, string> GetStepToCheckpointMap()
    {
        lock (_lock)
        {
            return _stepToCheckpointMap.ToImmutableDictionary();
        }
    }

    /// <summary>
    /// 扫描旧步骤文件并返回未迁移的记录列表(供重试入口使用)。
    /// </summary>
    public List<LegacyGitStepRecord> ScanUnmigratedSteps()
    {
        var state = LoadState();
        var results = new List<LegacyGitStepRecord>();

        if (!Directory.Exists(AppPaths.StepsDir))
            return results;

        foreach (var file in Directory.GetFiles(AppPaths.StepsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var record = JsonSerializer.Deserialize<GitStepRecord>(json, AppJsonContext.Default.GitStepRecord);
                if (record is not null && !state.MigratedStepIds.Contains(record.StepId))
                {
                    results.Add(LegacyGitStepRecordFromRecord(record));
                }
            }
            catch
            {
                // 跳过无法解析的文件
            }
        }

        return results;
    }

    #region Private Methods

    private MigrationItem MigrateSingle(string filePath, MigrationState state)
    {
        var item = new MigrationItem();
        try
        {
            var json = File.ReadAllText(filePath);
            var oldRecord = JsonSerializer.Deserialize<GitStepRecord>(json, AppJsonContext.Default.GitStepRecord);
            if (oldRecord is null)
            {
                item.Status = "failed";
                item.Reason = "无法反序列化旧步骤记录";
                return item;
            }

            // 检查是否已迁移过
            if (state.MigratedStepIds.Contains(oldRecord.StepId))
            {
                item.Status = "skipped";
                item.OldStepId = oldRecord.StepId;
                return item;
            }

            // 解析上下文: 优先 MergeCommit, 其次 StepBranch 存在则取 tip
            var context = ResolveContext(oldRecord);
            if (!context.IsMergeCommitAvailable && context.TipCommit is null)
            {
                item.Status = "failed";
                item.Reason = "无法解析 Git 上下文(既无 MergeCommit 也无有效 StepBranch)";
                item.ContextDetail = $"StepId={oldRecord.StepId}, BaseBranch={oldRecord.BaseBranch}, StepBranch={oldRecord.StepBranch}";
                return item;
            }

            // 创建新 Checkpoint
            var checkpoint = CreateCheckpoint(oldRecord, context);
            SaveCheckpoint(checkpoint);

            // 建立映射
            lock (_lock)
            {
                _stepToCheckpointMap[oldRecord.StepId] = checkpoint.CheckpointId;
            }

            item.OldStepId = oldRecord.StepId;
            item.NewCheckpointId = checkpoint.CheckpointId;
            item.Status = context.IsMergeCommitAvailable ? "migrated" : "migrated-with-warnings";
            item.ContextDetail = context.Detail;

            Log.Info("Migration", $"步骤 {oldRecord.StepId} → 检查点 {checkpoint.CheckpointId} (状态: {item.Status})");
            return item;
        }
        catch (Exception ex)
        {
            item.Status = "failed";
            item.Reason = ex.Message;
            Log.Warn("Migration", ex, $"迁移步骤失败: {filePath}");
            return item;
        }
    }

    /// <summary>
    /// 解析旧步骤的 Git 上下文: 优先 MergeCommit 可解析, 其次 StepBranch 存在则取 tip。
    /// 只对当前仓库可验证的记录迁移, 不猜 BaseBranch。
    /// </summary>
    private (bool IsMergeCommitAvailable, string Detail, string? MergeCommit, string? TipCommit) ResolveContext(GitStepRecord record)
    {
        // 优先: MergeCommit 可解析
        if (!string.IsNullOrEmpty(record.MergeCommit))
        {
            var verify = _git.Run("rev-parse", "--verify", record.MergeCommit);
            if (verify.Succeeded)
            {
                return (true, $"MergeCommit {record.MergeCommit} 可验证", record.MergeCommit, null);
            }
        }

        // 其次: StepBranch 存在则取 tip
        if (!string.IsNullOrEmpty(record.StepBranch))
        {
            var branches = _git.GetLocalBranches();
            if (branches.Contains(record.StepBranch, StringComparer.OrdinalIgnoreCase))
            {
                var rev = _git.Run("rev-parse", record.StepBranch);
                if (rev.Succeeded)
                {
                    return (false, $"StepBranch {record.StepBranch} tip: {rev.Stdout.Trim()}", null, rev.Stdout.Trim());
                }
            }
        }

        // 无法验证: 不猜 BaseBranch
        return (false, "无法在当前仓库中验证上下文", null, null);
    }

    private GitCheckpointRecord CreateCheckpoint(GitStepRecord record, (bool IsMergeCommitAvailable, string Detail, string? MergeCommit, string? TipCommit) context)
    {
        return new GitCheckpointRecord
        {
            Label = record.Label,
            SourceStepId = record.StepId,
            BaseBranch = record.BaseBranch,
            CommitHash = context.TipCommit ?? record.MergeCommit ?? string.Empty,
            MergeCommit = record.MergeCommit,
            CreatedAt = record.CreatedAt,
            CompletedAt = record.CompletedAt,
            Merged = record.Status == GitStepStatus.Merged || !string.IsNullOrEmpty(record.MergeCommit)
        };
    }

    private void SaveCheckpoint(GitCheckpointRecord checkpoint)
    {
        Directory.CreateDirectory(_checkpointsDir);
        var file = Path.Combine(_checkpointsDir, $"{checkpoint.CheckpointId}.json");
        var json = JsonSerializer.Serialize(checkpoint, SerializerOptions);
        File.WriteAllText(file, json);
    }

    private bool UpdateJsonReferences(List<MigrationItem> migratedItems)
    {
        var result = true;
        var mapping = migratedItems.ToDictionary(i => i.OldStepId, i => i.NewCheckpointId!);

        // 更新 assignments/*.json 中的 StepId
        if (Directory.Exists(AppPaths.AssignmentsDir))
        {
            foreach (var file in Directory.GetFiles(AppPaths.AssignmentsDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var node = JsonNode.Parse(json);
                    if (node is null) continue;

                    bool changed = false;
                    ReplaceStepIds(node, mapping, ref changed);

                    if (changed)
                    {
                        File.WriteAllText(file, node.ToJsonString(SerializerOptions));
                    }
                }
                catch
                {
                    result = false;
                }
            }
        }

        // 更新 sessions/*.json 中的 ToolSegment.StepId
        if (Directory.Exists(AppPaths.SessionsDir))
        {
            foreach (var file in Directory.GetFiles(AppPaths.SessionsDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var node = JsonNode.Parse(json);
                    if (node is null) continue;

                    bool changed = false;
                    ReplaceStepIds(node, mapping, ref changed);

                    if (changed)
                    {
                        File.WriteAllText(file, node.ToJsonString(SerializerOptions));
                    }
                }
                catch
                {
                    result = false;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 使用 JsonNode 递归替换 JSON 中的旧 stepId 为 checkpointId (AOT 安全)。
    /// </summary>
    private static void ReplaceStepIds(JsonNode node, Dictionary<string, string> mapping, ref bool changed)
    {
        if (node is JsonObject obj)
        {
            foreach (var kvp in obj)
            {
                var value = kvp.Value;
                if (value is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.String)
                {
                    var stringValue = jsonValue.ToString();
                    if (mapping.ContainsKey(stringValue))
                    {
                        obj[kvp.Key] = mapping[stringValue];
                        changed = true;
                    }
                }
                else
                {
                    if (value is not null)
                        ReplaceStepIds(value, mapping, ref changed);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                    ReplaceStepIds(item, mapping, ref changed);
            }
        }
    }

    private MigrationState LoadState()
    {
        try
        {
            if (File.Exists(MigrationStatePath))
            {
                var json = File.ReadAllText(MigrationStatePath);
                var state = JsonSerializer.Deserialize<MigrationState>(json, SerializerOptions);
                if (state is not null)
                    return state;
            }
        }
        catch
        {
            // 状态文件损坏则重新开始
        }
        return new MigrationState();
    }

    private void SaveState(MigrationState state)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var json = JsonSerializer.Serialize(state, SerializerOptions);
            File.WriteAllText(MigrationStatePath, json);
        }
        catch (Exception ex)
        {
            Log.Warn("Migration", ex, "保存迁移状态失败");
        }
    }

    private static string BuildSummaryMessage(MigrationSummary summary)
    {
        if (summary.FailedToMigrate > 0 && summary.MigratedSuccessfully > 0)
            return $"迁移部分完成: {summary.MigratedSuccessfully} 条成功, {summary.FailedToMigrate} 条失败, {summary.SkippedAlreadyMigrated} 条已跳过。失败项可重试。";
        if (summary.FailedToMigrate > 0)
            return $"迁移失败: {summary.FailedToMigrate} 条无法迁移。可重试。";
        if (summary.MigratedSuccessfully > 0)
            return $"迁移完成: {summary.MigratedSuccessfully} 条旧步骤已转为检查点。";
        return "迁移完成, 无旧步骤需要处理。";
    }

    private static LegacyGitStepRecord LegacyGitStepRecordFromRecord(GitStepRecord record)
    {
        return new LegacyGitStepRecord
        {
            StepId = record.StepId,
            Label = record.Label,
            BaseBranch = record.BaseBranch,
            StepBranch = record.StepBranch,
            Status = record.Status.ToString(),
            RollbackMode = record.RollbackMode,
            MergeCommit = record.MergeCommit,
            CreatedAt = record.CreatedAt,
            CompletedAt = record.CompletedAt
        };
    }

    #endregion
}
