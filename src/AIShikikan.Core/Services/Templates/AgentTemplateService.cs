using System.Text.Json;
using System.Text.Json.Serialization;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Templates;

public class AgentTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "expert";

    public List<string> Expertise { get; set; } = [];

    public string? DefaultAgentId { get; set; }

    public string? PersonaId { get; set; }

    public string SystemPrompt { get; set; } = string.Empty;

    [JsonIgnore]
    public string Display => string.IsNullOrEmpty(Name) ? Id : Name;
}

public static class AgentTemplateService
{
    public static IReadOnlyList<AgentTemplate> LoadAll()
    {
        var list = new List<AgentTemplate>();
        Directory.CreateDirectory(AppPaths.TemplatesDir);
        foreach (var file in Directory.GetFiles(AppPaths.TemplatesDir, "*.json"))
        {
            try
            {
                var t = JsonSerializer.Deserialize(File.ReadAllText(file), AppJsonContext.Default.AgentTemplate);
                if (t is not null && !string.IsNullOrEmpty(t.Id))
                {
                    if (string.IsNullOrEmpty(t.Name))
                    {
                        t.Name = t.Id;
                    }

                    list.Add(t);
                }
            }
            catch
            {
            }
        }

        return list;
    }

    public static AgentTemplate? Find(string? idOrName, IReadOnlyList<AgentTemplate> templates) =>
        templates.FirstOrDefault(t =>
            string.Equals(t.Id, idOrName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Name, idOrName, StringComparison.OrdinalIgnoreCase));

    public static string MatchTemplate(string task, IReadOnlyList<AgentTemplate> templates)
    {
        if (templates.Count == 0)
        {
            return string.Empty;
        }

        var best = templates[0];
        var bestScore = 0;
        foreach (var t in templates)
        {
            var score = t.Expertise.Count(k => task.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (score > bestScore)
            {
                best = t;
                bestScore = score;
            }
        }

        return bestScore > 0 ? best.Id : string.Empty;
    }

    public static void EnsureSamplesExist()
    {
        if (Directory.Exists(AppPaths.TemplatesDir) && Directory.GetFiles(AppPaths.TemplatesDir, "*.json").Length > 0)
        {
            return;
        }

        Save(new AgentTemplate
        {
            Id = "frontend-dev",
            Name = "前端程序员",
            Expertise = ["前端", "html", "css", "javascript", "typescript", "vue", "react", "组件", "ui", "页面", "样式"],
            DefaultAgentId = "claude",
            SystemPrompt = """
                            # 角色: 资深前端工程师
                            你负责前端相关任务的执行与审查:
                             - 组件化思维，拆分可复用组件，关注可访问性(a11y)与响应式
                             - 关注浏览器兼容与性能(渲染路径、bundle 体积)
                             - 样式与交互遵循现有代码风格，优先使用项目已引入的框架与工具
                             - 改动先聚焦最小可验证单元，输出变更说明与自测清单
                            """
        });

        Save(new AgentTemplate
        {
            Id = "backend-dev",
            Name = "后端程序员",
            Expertise = ["后端", "服务端", "api", "接口", "数据库", "sql", "并发", "中间件", "grpc", "微服务"],
            DefaultAgentId = "codex",
            SystemPrompt = """
                            # 角色: 资深后端工程师
                             - 分层架构: 接口层 / 业务层 / 数据层职责分离
                             - 接口设计遵循 REST 惯例, 参数校验与错误码统一
                             - 数据库: 索引与事务边界明确, 避免 N+1 与全表扫描
                             - 并发安全: 明确共享状态与锁/原子操作
                             - 输出代码附注影响面(变更的文件与潜在调用方)
                            """
        });

        Save(new AgentTemplate
        {
            Id = "web-researcher",
            Name = "专业联网搜索总结",
            Expertise = ["搜索", "调研", "查资料", "总结", "研究报告", "review", "文献", "新闻"],
            DefaultAgentId = "gemini",
            SystemPrompt = """
                            # 角色: 专业联网搜索与综述分析师
                             - 多源交叉验证: 至少 2-3 个独立来源确认同一事实再下结论
                             - 每个结论标注来源与时效(日期)
                             - 区分事实、推测与引述
                             - 输出结构化综述: 背景 / 关键信息(带来源) / 分歧点 / 结论
                            """
        });

        Save(new AgentTemplate
        {
            Id = "test-reviewer",
            Name = "测试回滚审查",
            Expertise = ["测试", "单测", "用例", "diff", "审查", "review", "回滚", "回归"],
            DefaultAgentId = "reasonix",
            SystemPrompt = """
                            # 角色: 测试与变更审查官
                            write-then-verify 工作流:
                             1. 先编写/补充测试用例, 再验证实现
                             2. 审查 diff: 逻辑变更与测试变更一一对应
                             3. 质量门禁: 覆盖率死角、边界值、异常路径
                             4. 回滚预案: 若变更失败, 给出明确的 git 回滚步骤与波及面
                             - 每次输出: 审查结论 / 测试清单 / 回滚预案
                            """
        });

        Save(new AgentTemplate
        {
            Id = "security-audit",
            Name = "安全审计",
            Expertise = ["安全", "审计", "漏洞", "密钥", "token", "owasp", "注入", "越权", "sast"],
            DefaultAgentId = "codex",
            SystemPrompt = """
                            # 角色: 安全审计专家(只审计, 不修改)
                            审查范围: 注入(XSS/SQL/命令)、越权/权限缺失、敏感信息泄露(密钥/token)、依赖漏洞、错误配置。
                            输出分级报告:
                             - CRITICAL / HIGH / MEDIUM / LOW 四级
                             - 每条: 位置(文件:行) / 问题描述 / 修复建议(不实施修改)
                             - 结束时给出整体风险总结与优先修复清单
                            """
        });
    }

    private static void Save(AgentTemplate template)
    {
        Directory.CreateDirectory(AppPaths.TemplatesDir);
        var file = Path.Combine(AppPaths.TemplatesDir, $"{template.Id}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(template, AppJsonContext.Default.AgentTemplate));
    }

    /// <summary>从外部 JSON 文件导入专家模板, 校验后保存到配置目录。
    /// 返回 (导入对象可为 null, 错误信息)。</summary>
    public static (AgentTemplate? Template, string? Error) ImportFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return (null, $"文件不存在: {path}");
        }

        AgentTemplate template;
        try
        {
            template = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.AgentTemplate)
                       ?? throw new InvalidDataException("文件内容不是有效的 Agent 模板(JSON)。");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"解析失败: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(template.Id))
        {
            return (null, "缺少 id 字段, 无法导入。");
        }

        if (string.IsNullOrWhiteSpace(template.Name))
        {
            template.Name = template.Id;
        }

        if (string.IsNullOrWhiteSpace(template.SystemPrompt))
        {
            return (null, "缺少 systemPrompt 字段, 无法导入。");
        }

        template.Id = EnsureUniqueId(template.Id, AppPaths.TemplatesDir);
        Save(template);
        return (template, null);
    }

    private static string EnsureUniqueId(string id, string dir)
    {
        var file = Path.Combine(dir, $"{id}.json");
        if (!File.Exists(file))
        {
            return id;
        }

        var n = 2;
        while (File.Exists(Path.Combine(dir, $"{id}-{n}.json")))
        {
            n++;
        }

        return $"{id}-{n}";
    }
}