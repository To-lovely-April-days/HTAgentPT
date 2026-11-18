namespace HT.Agent.Domain;

/// <summary>功能权限键（表 3-2 功能权限矩阵）。角色持有的键集合入库可配，接口以
/// [RequirePermission] 在入口处校验（FR-7.3），越权直接拒绝并记录。</summary>
public static class PermissionKeys
{
    /// <summary>检索问答（内部库，含私有库与共享库，按角色密级过滤）。</summary>
    public const string QaInternal = "qa.internal";
    /// <summary>检索问答（公开库）。</summary>
    public const string QaPublic = "qa.public";
    /// <summary>历史项目检索。客户角色由数据范围限定仅本人（FR-7.4），不单设键。</summary>
    public const string ProjectSearch = "project.search";
    /// <summary>方案与投标书生成。</summary>
    public const string Generate = "generate.doc";
    /// <summary>报价与合同拼装。</summary>
    public const string GenerateContract = "generate.contract";
    /// <summary>中英翻译。</summary>
    public const string Translate = "translate";
    /// <summary>故障案例录入。</summary>
    public const string CaseWrite = "case.write";
    /// <summary>故障案例检索。</summary>
    public const string CaseRead = "case.read";
    /// <summary>故障案例审核（仅总部审核人）。</summary>
    public const string CaseReview = "case.review";
    /// <summary>报修工单处理（售后）。</summary>
    public const string TicketHandle = "ticket.handle";
    /// <summary>客户自助：本人工单与设备。</summary>
    public const string CustomerSelf = "customer.self";
    /// <summary>语料管理：上传、解析、分块检视与编辑。</summary>
    public const string CorpusManage = "corpus.manage";
    /// <summary>往语料库上传文件（员工上传，管理员审核后才解析）。除外部客户外的内部角色都有。</summary>
    public const string CorpusUpload = "corpus.upload";
    /// <summary>知识库管理：建库、同步、发布、撤回。</summary>
    public const string KbManage = "kb.manage";
    /// <summary>元数据与词表、台账、术语维护。</summary>
    public const string MetaManage = "meta.manage";
    /// <summary>模板与条款库管理。</summary>
    public const string TemplateManage = "template.manage";
    /// <summary>用户与权限管理。</summary>
    public const string UserManage = "user.manage";
    /// <summary>系统设置：模型、运行参数、备份、远程接入。</summary>
    public const string SystemConfig = "system.config";
    /// <summary>审计查询与使用统计。</summary>
    public const string AuditRead = "audit.read";
    /// <summary>查看采购价格：价格库、助手查价格和材料成本估算。</summary>
    public const string PriceView = "price.view";
    /// <summary>管理价格库：上传价格表、上传新版本、删除。</summary>
    public const string PriceManage = "price.manage";
}

/// <summary>内置角色代码（表 3-1）。角色可增可配（FR-7.2），这五个是种子。</summary>
public static class RoleCodes
{
    public const string PreSales = "presales";
    public const string AfterSales = "aftersales";
    public const string Customer = "customer";
    public const string Admin = "admin";
    /// <summary>总部审核人：账号只存在于总部节点部署（3.4 第一条，歧义 3 的既定处理）。</summary>
    public const string HqReviewer = "hq_reviewer";
    /// <summary>采购：维护价格库。</summary>
    public const string Purchasing = "purchasing";
}
