using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using HT.Agent.Infrastructure.Templates;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

public class TemplateService(
    AppDbContext db,
    IFileStorage storage,
    IVocabService vocab,
    IAuditWriter audit,
    ICurrentUser me) : ITemplateService
{
    public async Task<TemplateUploadResult> UploadAsync(Stream file, string fileName, string name, string docType, CancellationToken ct = default)
    {
        if (!fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            throw new DomainRuleException("TEMPLATE_FORMAT", "模板须为 .docx（内容控件只存在于 docx）");
        if (!await vocab.IsValidAsync(VocabKeys.DocCategory, docType, ct))
            throw new DomainRuleException("TEMPLATE_DOCTYPE", $"文档类别「{docType}」不在受控词表中");

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        TemplateSlotExtractor.ExtractResult extracted;
        try { extracted = TemplateSlotExtractor.Extract(buffer); }
        catch (Exception ex) when (ex is InvalidDataException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or System.IO.FileFormatException)
        {
            throw new DomainRuleException("TEMPLATE_BAD_FILE", $"模板文件无法解析：{ex.Message}");
        }
        buffer.Position = 0;
        var fileKey = await storage.SaveAsync(buffer, fileName, ct);

        var template = new Template
        {
            Id = Guid.NewGuid(),
            Name = name,
            DocType = docType,
            FileKey = fileKey,
            IsEnabled = false, // 槽位定义补全前不可启用（FR-5.3）
            CreatedById = me.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        foreach (var s in extracted.Slots)
            template.Slots.Add(new TemplateSlot
            {
                Id = Guid.NewGuid(),
                Tag = s.Tag,
                // 控件标题（别名）自动抽为显示名称与章节（「章节/名称」约定）；
                // 没写标题的先用 Tag/空占位，待管理员在界面补全后方可启用
                Name = s.Name ?? s.Tag,
                Section = s.Section ?? "",
                DataType = s.DataType,
                Unit = s.Unit,          // 模板里控件后面跟的单位文字（ml / MPa / ℃…）
                Choices = s.Choices,
                Required = true,
                Stage = SlotStage.Current,
                ForbidInherit = s.ForbidInherit, // FR-5.7 默认清单命中的先勾上
                Prompt = s.Prompt,     // 占位文字自动抽取为提问话术初值（FR-5.3）
                SubFields = s.SubFields,
                SortOrder = s.SortOrder
            });
        db.Templates.Add(template);
        await db.SaveChangesAsync(ct);
        await Log("template.upload", template.Id, new { name, docType, slots = extracted.Slots.Count, warnings = extracted.Warnings.Count }, ct);
        return new TemplateUploadResult(template.Id, extracted.Slots.Count, extracted.Warnings);
    }

    public async Task<IReadOnlyList<TemplateRow>> ListAsync(bool includeDisabled, CancellationToken ct = default)
    {
        var rows = await db.Templates.AsNoTracking()
            .Where(t => includeDisabled || t.IsEnabled)
            .Select(t => new
            {
                t.Id, t.Name, t.DocType, t.IsEnabled, t.UpdatedAt,
                SlotCount = t.Slots.Count,
                Incomplete = t.Slots.Count(s => s.Name == s.Tag || s.Section == ""),
                LastUsedAt = db.GenerationSessions.Where(g => g.TemplateId == t.Id)
                    .Max(g => (DateTimeOffset?)g.UpdatedAt)
            })
            .OrderByDescending(t => t.LastUsedAt ?? t.UpdatedAt)
            .ToListAsync(ct);
        return rows.Select(t => new TemplateRow(t.Id, t.Name, t.DocType, t.IsEnabled, t.SlotCount,
            t.Incomplete, t.UpdatedAt, t.LastUsedAt)).ToList();
    }

    public async Task<TemplateDetail?> GetAsync(Guid templateId, CancellationToken ct = default)
    {
        var t = await db.Templates.AsNoTracking().Include(x => x.Slots)
            .FirstOrDefaultAsync(x => x.Id == templateId, ct);
        return t is null ? null : new TemplateDetail(t.Id, t.Name, t.DocType, t.IsEnabled,
            t.Slots.OrderBy(s => s.SortOrder).Select(ToDef).ToList());
    }

    public async Task UpdateSlotAsync(Guid slotId, SlotDefEdit edit, CancellationToken ct = default)
    {
        var slot = await db.TemplateSlots.Include(s => s.Template)
            .FirstOrDefaultAsync(s => s.Id == slotId, ct)
            ?? throw new DomainRuleException("SLOT_NOT_FOUND", "槽位不存在");
        if (string.IsNullOrWhiteSpace(edit.Name) || string.IsNullOrWhiteSpace(edit.Section))
            throw new DomainRuleException("SLOT_DEF_REQUIRED", "显示名称与所属章节必填（表 5-2）");
        slot.Name = edit.Name.Trim();
        slot.Section = edit.Section.Trim();
        slot.DataType = edit.DataType;
        slot.Unit = edit.Unit;
        slot.Choices = edit.Choices;
        slot.Required = edit.Required;
        slot.Stage = edit.Stage;
        slot.ForbidInherit = edit.ForbidInherit;
        slot.Prompt = edit.Prompt;
        slot.SuggestSource = edit.SuggestSource;
        slot.SubFields = edit.SubFields;
        slot.SortOrder = edit.SortOrder;
        slot.Template!.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await Log("template.slot_update", slot.TemplateId, new { slot.Tag }, ct);
    }

    public async Task EnableAsync(Guid templateId, bool enable, CancellationToken ct = default)
    {
        var t = await db.Templates.Include(x => x.Slots).FirstOrDefaultAsync(x => x.Id == templateId, ct)
            ?? throw new DomainRuleException("TEMPLATE_NOT_FOUND", "模板不存在");
        if (enable)
        {
            if (t.Slots.Count == 0)
                throw new DomainRuleException("TEMPLATE_NO_SLOTS", "没有槽位的模板无法进入交互流程（FR-5.3）");
            var incomplete = t.Slots.Where(s => s.Name == s.Tag || s.Section == "").Select(s => s.Tag).ToList();
            if (incomplete.Count > 0)
                throw new DomainRuleException("TEMPLATE_SLOTS_INCOMPLETE",
                    $"槽位定义不完整（缺显示名称或章节）：{string.Join("、", incomplete.Take(8))}{(incomplete.Count > 8 ? " 等" : "")}");
        }
        t.IsEnabled = enable;
        t.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await Log(enable ? "template.enable" : "template.disable", templateId, null, ct);
    }

    public async Task<Guid> CopyAsync(Guid templateId, string newName, CancellationToken ct = default)
    {
        var src = await db.Templates.AsNoTracking().Include(x => x.Slots)
            .FirstOrDefaultAsync(x => x.Id == templateId, ct)
            ?? throw new DomainRuleException("TEMPLATE_NOT_FOUND", "模板不存在");
        string copyKey;
        await using (var f = await storage.OpenAsync(src.FileKey, ct))
            copyKey = await storage.SaveAsync(f, $"copy_{src.Name}.docx", ct);
        var copy = new Template
        {
            Id = Guid.NewGuid(),
            Name = newName,
            DocType = src.DocType,
            FileKey = copyKey,
            IsEnabled = false,
            CreatedById = me.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        foreach (var s in src.Slots)
            copy.Slots.Add(new TemplateSlot
            {
                Id = Guid.NewGuid(), Tag = s.Tag, Name = s.Name, Section = s.Section,
                DataType = s.DataType, Unit = s.Unit, Choices = s.Choices, Required = s.Required,
                Stage = s.Stage, ForbidInherit = s.ForbidInherit, Prompt = s.Prompt,
                SuggestSource = s.SuggestSource, SubFields = s.SubFields, SortOrder = s.SortOrder
            });
        db.Templates.Add(copy);
        await db.SaveChangesAsync(ct);
        await Log("template.copy", copy.Id, new { from = templateId, newName }, ct);
        return copy.Id;
    }

    internal static SlotDef ToDef(TemplateSlot s) => new(s.Id, s.Tag, s.Name, s.Section, s.DataType,
        s.Unit, s.Choices, s.Required, s.Stage, s.ForbidInherit, s.Prompt, s.SuggestSource, s.SubFields, s.SortOrder);

    private Task Log(string action, Guid templateId, object? detail, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "template", TargetId: templateId.ToString(), Detail: detail), ct);
}
