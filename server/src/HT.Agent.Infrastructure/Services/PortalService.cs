using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HT.Agent.Infrastructure.Services;

/// <summary>隔离区客户门户（K 组）。全部匿名可用——登录不是前置门：账号由售后在处理报修时
/// 开通（FR-7.4），做成前置门就成了「没账号报不了修、没报修拿不到账号」的死循环。
/// 检索面只有公开库；文案与返回里不出现任何内部概念；已撤回内容一律表现为未找到。</summary>
public class PortalService(AppDbContext db, IRuntimeConfig config, IAuditWriter audit) : IPortalService
{
    public async Task<IReadOnlyList<PortalHit>> SearchAsync(string query, CancellationToken ct = default)
    {
        var tsQuery = ChineseTokenizer.ToTsQuery(query ?? "");
        if (string.IsNullOrEmpty(tsQuery)) return [];

        // 仅公开库 + 仅关键词路径：匿名端不做向量召回，避免把嵌入服务暴露给未鉴权流量
        var sql = """
            SELECT d.title AS "DocTitle", c.section_path AS "Section", c.page_no AS "PageNo",
                   LEFT(c.text, 240) AS "Excerpt"
            FROM chunk c
            JOIN document d ON d.id = c.doc_id
            WHERE c.is_active
              AND c.classification = 'Public'
              AND d.parse_status = 'Parsed'
              AND NOT d.is_withdrawn
              AND c.kb_id IN (SELECT kb.id FROM knowledge_base kb WHERE kb.is_active AND kb.tier = 'Public')
              AND to_tsvector('simple', c.search_text) @@ to_tsquery('simple', @tsq)
            ORDER BY ts_rank_cd(to_tsvector('simple', c.search_text), to_tsquery('simple', @tsq)) DESC
            LIMIT 6
            """;
        return await db.Database.SqlQueryRaw<PortalHit>(sql, new NpgsqlParameter("tsq", tsQuery)).ToListAsync(ct);
    }

    public async Task<PortalTicketCreated> CreateTicketAsync(PortalRepairRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Description))
            throw new DomainRuleException("TICKET_FIELDS", "请描述设备出现的情况");
        if (string.IsNullOrWhiteSpace(req.Contact) || req.Contact.Trim().Length < 6)
            throw new DomainRuleException("TICKET_CONTACT", "请留可联系到你的电话——查询进度时也要用它核对身份");

        // 设备编号可不填；填了且能对上台账，则挂到对应客户名下，售后开户时省一步
        string customerNo = "GUEST";
        var deviceNo = req.DeviceNo?.Trim() ?? "";
        if (deviceNo.Length > 0)
        {
            var device = await db.CustomerDevices.AsNoTracking()
                .FirstOrDefaultAsync(d => d.DeviceNo == deviceNo, ct);
            if (device is not null) customerNo = device.CustomerNo;
        }
        if (deviceNo.Length == 0) deviceNo = string.IsNullOrWhiteSpace(req.Model) ? "未提供" : $"型号 {req.Model!.Trim()}";

        var description = req.Description.Trim();
        if (!string.IsNullOrWhiteSpace(req.CustomerName))
            description = $"[单位：{req.CustomerName!.Trim()}] {description}";

        var prefix = await config.GetStringAsync(ConfigKeys.TicketNoPrefix, "RT", ct);
        var year = DateTimeOffset.UtcNow.Year;
        for (var attempt = 0; ; attempt++)
        {
            var seq = await db.Tickets.CountAsync(t => t.TicketNo.StartsWith($"{prefix}-{year}-"), ct) + 1 + attempt;
            var ticket = new Ticket
            {
                Id = Guid.NewGuid(),
                TicketNo = $"{prefix}-{year}-{seq:D4}",
                CustomerNo = customerNo,
                DeviceNo = deviceNo,
                Description = description,
                Contact = req.Contact.Trim(),
                Status = TicketStatus.Submitted,
                Updates = JsonSerializer.Serialize(new List<TicketTrailEntry>
                {
                    new(DateTimeOffset.UtcNow, "客户（未登录）", TicketStatus.Submitted.ToString(), "客户经门户提交报修", ForCustomer: true)
                }),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Tickets.Add(ticket);
            try
            {
                await db.SaveChangesAsync(ct);
                await audit.WriteAsync(new AuditEntry("portal.ticket.create", AuditResult.Success,
                    Username: "portal-anonymous", TargetType: "ticket", TargetId: ticket.TicketNo,
                    Detail: new { customerNo, deviceNo }), ct);
                return new PortalTicketCreated(ticket.TicketNo, ticket.CreatedAt);
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                db.Entry(ticket).State = EntityState.Detached; // 单号并发撞车：换号重试
            }
        }
    }

    public async Task<PortalTicketView?> TrackAsync(string ticketNo, string contact, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticketNo) || string.IsNullOrWhiteSpace(contact)) return null;
        var ticket = await db.Tickets.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TicketNo == ticketNo.Trim(), ct);
        // 单号 + 提交时留的联系方式同时匹配才返回；不区分「单号不存在」与「联系方式不符」，
        // 否则查询口就成了单号枚举器
        if (ticket is null || !string.Equals(ticket.Contact, contact.Trim(), StringComparison.Ordinal))
        {
            await audit.WriteAsync(new AuditEntry("portal.ticket.track", AuditResult.Denied,
                Username: "portal-anonymous", TargetType: "ticket", TargetId: ticketNo.Trim()), ct);
            return null;
        }

        var trail = ticket.Updates is null
            ? []
            : JsonSerializer.Deserialize<List<TicketTrailEntry>>(ticket.Updates) ?? new List<TicketTrailEntry>();
        // 客户侧只呈现状态节点与对客留言；内部记录（技术判断、备件信息）不透出（K5）
        var nodes = trail
            .Select(e => new PortalTicketNode(e.At, e.Status, e.ForCustomer ? e.Note : null))
            .ToList();
        return new PortalTicketView(ticket.TicketNo, ticket.Status.ToString(), ticket.DeviceNo, ticket.CreatedAt, nodes);
    }
}
