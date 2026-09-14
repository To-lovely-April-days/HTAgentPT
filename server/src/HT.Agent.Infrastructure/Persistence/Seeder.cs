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
    public static async Task SeedAsync(AppDbContext db, IPasswordHasher hasher, IConfiguration config, CancellationToken ct = default)
    {
        if (await db.Companies.AnyAsync(ct)) return; // 已初始化

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
                PermissionKeys.CaseRead
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
                PermissionKeys.TicketHandle
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
                PermissionKeys.AuditRead
            ],
            Note = "问答默认关闭；确需开通须单独授权并记录（3.2/3.4）"
        };
        // 总部审核人：仅共享库（数据范围在检索层强制），账号只应存在于总部节点部署
        var reviewer = new Role
        {
            Id = Guid.NewGuid(), Code = RoleCodes.HqReviewer, Name = "总部审核人", IsSystem = true,
            Classifications = [Classification.Internal, Classification.Public],
            Permissions = [PermissionKeys.QaInternal, PermissionKeys.QaPublic, PermissionKeys.CaseReview]
        };
        db.Roles.AddRange(presales, aftersales, customer, admin, reviewer);

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
        string[] categories = ["方案", "合同", "报价", "图纸", "交付报告", "故障记录", "手册", "资料"];
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

        await db.SaveChangesAsync(ct);
    }
}
