using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace HT.Agent.Infrastructure.Persistence;

/// <summary>初始数据。判定标准（10.4）：全新服务器部署后，除填写配置外不需要改代码即可运行——
/// 公司名、角色映射、词表取值全部是数据，这里只提供默认值。</summary>
public static class Seeder
{
    /// <summary>上线后才加的固定取值：老库里没有就补上（技术协议模板的文档类别）。
    /// 管理员停用过的不会被补回来——停用的行还在，只看有没有这一行。</summary>
    private static readonly (string Key, string Value)[] AddedTerms = [(VocabKeys.DocCategory, "技术协议"), (VocabKeys.DocCategory, "标准规范")];

    /// <summary>采购角色（价格库上线时加的）。已经有了就什么都不做——管理员之后改过的权限不会被改回来。</summary>
    private static async Task EnsurePurchasingAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Roles.AnyAsync(r => r.Code == RoleCodes.Purchasing, ct)) return;
        db.Roles.Add(new Role
        {
            Id = Guid.NewGuid(), Code = RoleCodes.Purchasing, Name = "采购", IsSystem = true,
            Classifications = [Classification.Internal, Classification.Public],
            Permissions = [PermissionKeys.QaInternal, PermissionKeys.QaPublic, PermissionKeys.CorpusUpload, PermissionKeys.PriceView, PermissionKeys.PriceManage],
        });
        var admin = await db.Roles.FirstOrDefaultAsync(r => r.Code == RoleCodes.Admin, ct);
        if (admin is not null)
        {
            var perms = admin.Permissions.ToList();
            foreach (var p in new[] { PermissionKeys.PriceView, PermissionKeys.PriceManage })
                if (!perms.Contains(p)) perms.Add(p);
            admin.Permissions = perms;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>系统自带的技能（Skills/*.md，SKILL.md 格式）：
    /// · 公司还没有这个代号的技能 → 加上；
    /// · 已有、但从没被人在后台改过（UpdatedById 为空）且内容和这版不同 → 换成这版，留一个版本记录；
    /// · 有人改过的一律不动。</summary>
    private static async Task EnsureSkillsAsync(AppDbContext db, CancellationToken ct)
    {
        var asm = typeof(Seeder).Assembly;
        var names = asm.GetManifestResourceNames().Where(n => n.StartsWith("skills/") && n.EndsWith(".md")).OrderBy(n => n).ToList();
        if (names.Count == 0) return;
        var seeds = new List<(HT.Agent.Application.Logic.SkillFiles.Parsed P, string Title, string Category, string Source, SkillStatus Status)>();
        foreach (var n in names)
        {
            using var stream = asm.GetManifestResourceStream(n)!;
            using var reader = new StreamReader(stream);
            var p = HT.Agent.Application.Logic.SkillFiles.Parse(await reader.ReadToEndAsync(ct));
            if (string.IsNullOrWhiteSpace(p.Name)) continue;
            var st = p.Meta.GetValueOrDefault("status") switch { "enabled" => SkillStatus.Enabled, "review" => SkillStatus.Review, _ => SkillStatus.Draft };
            seeds.Add((p, p.Meta.GetValueOrDefault("title") ?? p.Name, p.Meta.GetValueOrDefault("category") ?? "其他", p.Meta.GetValueOrDefault("source") ?? "own", st));
        }
        var changed = false;
        foreach (var cid in await db.Companies.Select(c => c.Id).ToListAsync(ct))
        {
            var existing = await db.AssistantSkills.Where(s => s.CompanyId == cid).ToDictionaryAsync(s => s.Code, ct);
            var now = DateTimeOffset.UtcNow;
            foreach (var (p, title, category, source, status) in seeds)
            {
                if (!existing.TryGetValue(p.Name, out var skill))
                {
                    skill = new AssistantSkill
                    {
                        Id = Guid.NewGuid(), CompanyId = cid, Code = p.Name, Name = title, Category = category, Description = p.Description,
                        Body = p.Body, Source = source, Status = status, Version = 1, UpdatedAt = now, CreatedAt = now,
                    };
                    db.AssistantSkills.Add(skill);
                    db.AssistantSkillVersions.Add(new AssistantSkillVersion
                    {
                        SkillId = skill.Id, Version = 1, Name = skill.Name, Description = skill.Description, Body = skill.Body, Note = "系统自带", EditedAt = now,
                    });
                    changed = true;
                    continue;
                }
                if (skill.UpdatedById is not null) continue;   // 有人改过，不动
                if (skill.Body == p.Body && skill.Description == p.Description && skill.Name == title && skill.Status == status) continue;
                skill.Name = title; skill.Category = category; skill.Description = p.Description; skill.Body = p.Body;
                skill.Source = source; skill.Status = status; skill.Version++; skill.UpdatedAt = now;
                db.AssistantSkillVersions.Add(new AssistantSkillVersion
                {
                    SkillId = skill.Id, Version = skill.Version, Name = skill.Name, Description = skill.Description, Body = skill.Body, Note = "系统更新自带的技能", EditedAt = now,
                });
                changed = true;
            }
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureTermsAsync(AppDbContext db, CancellationToken ct)
    {
        var added = false;
        foreach (var (key, value) in AddedTerms)
        {
            if (await db.VocabTerms.AnyAsync(v => v.VocabKey == key && v.Value == value, ct)) continue;
            var order = await db.VocabTerms.Where(v => v.VocabKey == key).Select(v => (int?)v.SortOrder).MaxAsync(ct) ?? -1;
            db.VocabTerms.Add(new VocabTerm { Id = Guid.NewGuid(), VocabKey = key, Value = value, SortOrder = order + 1 });
            added = true;
        }
        if (added) await db.SaveChangesAsync(ct);
    }

    public static async Task SeedAsync(AppDbContext db, IPasswordHasher hasher, IConfiguration config, CancellationToken ct = default)
    {
        if (await db.Companies.AnyAsync(ct))
        {
            await EnsureTermsAsync(db, ct); // 已初始化：只补后来新增的固定取值
            await EnsurePurchasingAsync(db, ct);
            await EnsureSkillsAsync(db, ct);
            return;
        }

        var company = new Company
        {
            Id = Guid.NewGuid(),
            Code = config["Seed:CompanyCode"] ?? "HT",
            Name = config["Seed:CompanyName"] ?? "上海霍桐实验仪器有限公司",
            ShortName = config["Seed:CompanyShortName"] ?? "上海霍桐",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Companies.Add(company);

        // 角色与可访问密级（表 3-1）、功能权限（表 3-2）
        var presales = new Role
        {
            Id = Guid.NewGuid(), Code = RoleCodes.PreSales, Name = "售前工程师", IsSystem = true,
            Classifications = [Classification.Confidential, Classification.Internal, Classification.Public],
            Permissions =
            [
                PermissionKeys.QaInternal, PermissionKeys.QaPublic, PermissionKeys.ProjectSearch,
                PermissionKeys.Generate, PermissionKeys.GenerateContract, PermissionKeys.Translate,
                PermissionKeys.CaseRead, PermissionKeys.CorpusUpload
            ]
        };
        var aftersales = new Role
        {
            Id = Guid.NewGuid(), Code = RoleCodes.AfterSales, Name = "售后工程师", IsSystem = true,
            Classifications = [Classification.Internal, Classification.Public],
            Permissions =
            [
                PermissionKeys.QaInternal, PermissionKeys.QaPublic, PermissionKeys.ProjectSearch,
                PermissionKeys.Translate, PermissionKeys.CaseRead, PermissionKeys.CaseWrite,
                PermissionKeys.TicketHandle, PermissionKeys.CorpusUpload
            ]
        };
        var customer = new Role
        {
            Id = Guid.NewGuid(), Code = RoleCodes.Customer, Name = "外部客户", IsSystem = true,
            Classifications = [Classification.Public],
            Permissions = [PermissionKeys.QaPublic, PermissionKeys.CustomerSelf]
        };
        // 管理员：默认不开放问答（表 3-1：确需开通时单独授权并记录——即给角色加 qa.* 键，动作本身入审计）
        var admin = new Role
        {
            Id = Guid.NewGuid(), Code = RoleCodes.Admin, Name = "系统管理员", IsSystem = true,
            Classifications = [Classification.Confidential, Classification.Internal, Classification.Public],
            Permissions =
            [
                PermissionKeys.CorpusManage, PermissionKeys.KbManage, PermissionKeys.MetaManage,
                PermissionKeys.TemplateManage, PermissionKeys.UserManage, PermissionKeys.SystemConfig,
                PermissionKeys.AuditRead, PermissionKeys.CorpusUpload
            ],
            Note = "问答默认关闭；确需开通须单独授权并记录（3.2/3.4）"
        };
        // 总部审核人：仅共享库（数据范围在检索层强制），账号只应存在于总部节点部署
        var reviewer = new Role
        {
            Id = Guid.NewGuid(), Code = RoleCodes.HqReviewer, Name = "总部审核人", IsSystem = true,
            Classifications = [Classification.Internal, Classification.Public],
            Permissions = [PermissionKeys.QaInternal, PermissionKeys.QaPublic, PermissionKeys.CaseReview, PermissionKeys.CorpusUpload]
        };
        db.Roles.AddRange(presales, aftersales, customer, admin, reviewer);
        await db.SaveChangesAsync(ct);
        await EnsurePurchasingAsync(db, ct);

        var adminPassword = config["Seed:AdminPassword"] ?? "Admin@12345";
        db.Users.Add(new AppUser
        {
            Id = Guid.NewGuid(),
            Username = config["Seed:AdminUsername"] ?? "admin",
            DisplayName = "系统管理员",
            PasswordHash = hasher.Hash(adminPassword),
            RoleId = admin.Id,
            CompanyId = company.Id,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // 三级库（FR-2.1）：私有库与公开库属本公司；共享库属集团（总部节点写，其余只读）
        db.KnowledgeBases.AddRange(
            new KnowledgeBase
            {
                Id = Guid.NewGuid(), Name = "本公司私有库", Tier = KnowledgeBaseTier.Private,
                CompanyId = company.Id, DefaultChunkStrategy = ChunkStrategy.General,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new KnowledgeBase
            {
                Id = Guid.NewGuid(), Name = "对外公开库", Tier = KnowledgeBaseTier.Public,
                CompanyId = company.Id, DefaultChunkStrategy = ChunkStrategy.General,
                Description = "仅承载审定发布的副本（FR-2.3）",
                CreatedAt = DateTimeOffset.UtcNow
            },
            new KnowledgeBase
            {
                Id = Guid.NewGuid(), Name = "集团共享库", Tier = KnowledgeBaseTier.Shared,
                CompanyId = null, DefaultChunkStrategy = ChunkStrategy.General,
                Description = "总部下发，本地只读（FR-2.2 单向覆盖）",
                CreatedAt = DateTimeOffset.UtcNow
            });

        // 受控词表（表 4-5）：文档类别八个固定取值；设备类型与客户为示例，管理员在词表界面维护
        string[] categories = ["方案", "合同", "报价", "图纸", "交付报告", "故障记录", "手册", "资料", "技术协议"];
        for (var i = 0; i < categories.Length; i++)
            db.VocabTerms.Add(new VocabTerm
            {
                Id = Guid.NewGuid(), VocabKey = VocabKeys.DocCategory, Value = categories[i], SortOrder = i
            });
        string[] deviceTypes = ["反应釜", "光化学反应仪", "平行合成仪", "旋转蒸发仪", "真空干燥箱"];
        for (var i = 0; i < deviceTypes.Length; i++)
            db.VocabTerms.Add(new VocabTerm
            {
                Id = Guid.NewGuid(), VocabKey = VocabKeys.DeviceType, Value = deviceTypes[i], SortOrder = i
            });

        // 部署期配置种子：Seed:Config 段（部署包用环境变量 Seed__Config__sync.token 这类形式传入）
        // 在首次初始化时直接落 sys_config，节点同步等跨节点配置起来即可用，不必先进「系统设置」逐项填。
        // 已初始化的库不会再走到这里——之后改配置一律走界面（改后即时生效）。
        foreach (var item in config.GetSection("Seed:Config").GetChildren())
        {
            if (string.IsNullOrEmpty(item.Value)) continue;
            db.SysConfigs.Add(new SysConfig
            {
                Key = item.Key,
                Value = System.Text.Json.JsonSerializer.Serialize(item.Value),
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
        await EnsureSkillsAsync(db, ct);
        await EnsureTermsAsync(db, ct);
    }
}
