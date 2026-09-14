using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    username = table.Column<string>(type: "text", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: true),
                    target_id = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<string>(type: "jsonb", nullable: true),
                    result = table.Column<string>(type: "text", nullable: false),
                    ip = table.Column<string>(type: "text", nullable: true),
                    terminal_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => new { x.id, x.at });
                });

            migrationBuilder.CreateTable(
                name: "backup_run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ok = table.Column<bool>(type: "boolean", nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    target = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    acknowledged_by = table.Column<Guid>(type: "uuid", nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clause",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: true),
                    supersedes_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clause", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "company",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    short_name = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_company", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customer_device",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_no = table.Column<string>(type: "text", nullable: false),
                    device_no = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    project_no = table.Column<string>(type: "text", nullable: true),
                    delivered_at = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_device", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fault_case",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_no = table.Column<string>(type: "text", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_model = table.Column<string>(type: "text", nullable: false),
                    alarm_code = table.Column<string>(type: "text", nullable: true),
                    phenomenon = table.Column<string>(type: "text", nullable: false),
                    cause_analysis = table.Column<string>(type: "text", nullable: false),
                    steps = table.Column<string>(type: "text", nullable: false),
                    spare_parts = table.Column<string>(type: "text", nullable: true),
                    result = table.Column<string>(type: "text", nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: true),
                    sync_status = table.Column<string>(type: "text", nullable: false),
                    chunk_id = table.Column<long>(type: "bigint", nullable: true),
                    source_company = table.Column<string>(type: "text", nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reject_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fault_case", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project",
                columns: table => new
                {
                    project_no = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_name = table.Column<string>(type: "text", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    device_type = table.Column<string>(type: "text", nullable: false),
                    device_model = table.Column<string>(type: "text", nullable: true),
                    spec_params = table.Column<string>(type: "text", nullable: true),
                    contract_amount = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    delivery_status = table.Column<string>(type: "text", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    doc_path = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project", x => x.project_no);
                });

            migrationBuilder.CreateTable(
                name: "publish_record",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_doc_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_doc_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_key = table.Column<string>(type: "text", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    withdraw_reason = table.Column<string>(type: "text", nullable: true),
                    withdrawn_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_publish_record", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "qa_session",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_qa_session", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "role",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    classifications = table.Column<string>(type: "jsonb", nullable: false),
                    permissions = table.Column<string>(type: "jsonb", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sync_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    batch_no = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ok = table.Column<bool>(type: "boolean", nullable: true),
                    detail = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sys_config",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sys_config", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "template",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    doc_type = table.Column<string>(type: "text", nullable: false),
                    file_key = table.Column<string>(type: "text", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "term",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain = table.Column<string>(type: "text", nullable: false),
                    zh = table.Column<string>(type: "text", nullable: false),
                    en = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    submitted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_term", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ticket",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_no = table.Column<string>(type: "text", nullable: false),
                    customer_no = table.Column<string>(type: "text", nullable: false),
                    device_no = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    contact = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    assignee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updates = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vocab_term",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vocab_key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    aliases = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vocab_term", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_base",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    tier = table.Column<string>(type: "text", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    default_chunk_strategy = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_base", x => x.id);
                    table.ForeignKey(
                        name: "fk_knowledge_base_company_company_id",
                        column: x => x.company_id,
                        principalTable: "company",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "qa_message",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question = table.Column<string>(type: "text", nullable: false),
                    rewritten_query = table.Column<string>(type: "text", nullable: true),
                    intent = table.Column<string>(type: "text", nullable: true),
                    answer = table.Column<string>(type: "text", nullable: true),
                    no_result_hints = table.Column<string>(type: "text", nullable: true),
                    sources = table.Column<string>(type: "jsonb", nullable: true),
                    helpful = table.Column<bool>(type: "boolean", nullable: true),
                    feedback_reason = table.Column<string>(type: "text", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_qa_message", x => x.id);
                    table.ForeignKey(
                        name: "fk_qa_message__qa_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "qa_session",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "app_user",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    employee_no = table.Column<string>(type: "text", nullable: true),
                    department = table.Column<string>(type: "text", nullable: true),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    customer_no = table.Column<string>(type: "text", nullable: true),
                    role_id = table.Column<Guid>(type: "uuid", nullable: true),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    active_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    active_terminal_id = table.Column<string>(type: "text", nullable: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    password_reset_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    password_reset_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_user", x => x.id);
                    table.ForeignKey(
                        name: "fk_app_user__companies_company_id",
                        column: x => x.company_id,
                        principalTable: "company",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_app_user__roles_role_id",
                        column: x => x.role_id,
                        principalTable: "role",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "generation_session",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_hint = table.Column<string>(type: "text", nullable: true),
                    base_project_no = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    slot_values = table.Column<string>(type: "jsonb", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    output_file_key = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generation_session", x => x.id);
                    table.ForeignKey(
                        name: "fk_generation_session__templates_template_id",
                        column: x => x.template_id,
                        principalTable: "template",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "template_slot",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    section = table.Column<string>(type: "text", nullable: false),
                    data_type = table.Column<string>(type: "text", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: true),
                    choices = table.Column<string>(type: "jsonb", nullable: true),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    stage = table.Column<string>(type: "text", nullable: false),
                    forbid_inherit = table.Column<bool>(type: "boolean", nullable: false),
                    prompt = table.Column<string>(type: "text", nullable: true),
                    suggest_source = table.Column<string>(type: "text", nullable: true),
                    sub_fields = table.Column<string>(type: "jsonb", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_slot", x => x.id);
                    table.ForeignKey(
                        name: "fk_template_slot_template_template_id",
                        column: x => x.template_id,
                        principalTable: "template",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kb_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    file_key = table.Column<string>(type: "text", nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: true),
                    classification = table.Column<string>(type: "text", nullable: false),
                    parse_status = table.Column<string>(type: "text", nullable: false),
                    parse_error = table.Column<string>(type: "text", nullable: true),
                    chunk_strategy_override = table.Column<string>(type: "text", nullable: true),
                    parse_version = table.Column<int>(type: "integer", nullable: false),
                    is_withdrawn = table.Column<bool>(type: "boolean", nullable: false),
                    uploaded_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    parsed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document", x => x.id);
                    table.ForeignKey(
                        name: "fk_document__knowledge_bases_kb_id",
                        column: x => x.kb_id,
                        principalTable: "knowledge_base",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_terminal",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    terminal_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    bound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_ip = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_terminal", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_terminal_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chunk",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    doc_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kb_id = table.Column<Guid>(type: "uuid", nullable: false),
                    classification = table.Column<string>(type: "text", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    section_path = table.Column<string>(type: "text", nullable: true),
                    page_no = table.Column<int>(type: "integer", nullable: true),
                    bbox = table.Column<string>(type: "text", nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    search_text = table.Column<string>(type: "text", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1024)", nullable: true),
                    embedding_model = table.Column<string>(type: "text", nullable: true),
                    parse_version = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chunk", x => x.id);
                    table.ForeignKey(
                        name: "fk_chunk__documents_doc_id",
                        column: x => x.doc_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "doc_metadata",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_no = table.Column<string>(type: "text", nullable: true),
                    customer_name = table.Column<string>(type: "text", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    device_type = table.Column<string>(type: "text", nullable: false),
                    doc_category = table.Column<string>(type: "text", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    doc_version = table.Column<string>(type: "text", nullable: true),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_doc_metadata", x => x.document_id);
                    table.ForeignKey(
                        name: "fk_doc_metadata__documents_document_id",
                        column: x => x.document_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "parse_job",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    doc_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    chunk_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    queued_by = table.Column<Guid>(type: "uuid", nullable: false),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parse_job", x => x.id);
                    table.ForeignKey(
                        name: "fk_parse_job_document_doc_id",
                        column: x => x.doc_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_app_user_company_id",
                table: "app_user",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_app_user_customer_no",
                table: "app_user",
                column: "customer_no");

            migrationBuilder.CreateIndex(
                name: "ix_app_user_role_id",
                table: "app_user",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_app_user_username",
                table: "app_user",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_action_at",
                table: "audit_log",
                columns: new[] { "action", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_at",
                table: "audit_log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_user_id_at",
                table: "audit_log",
                columns: new[] { "user_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_chunk_doc_id",
                table: "chunk",
                column: "doc_id");

            migrationBuilder.CreateIndex(
                name: "ix_chunk_kb_id_classification",
                table: "chunk",
                columns: new[] { "kb_id", "classification" });

            migrationBuilder.CreateIndex(
                name: "ix_clause_category_code",
                table: "clause",
                columns: new[] { "category", "code" });

            migrationBuilder.CreateIndex(
                name: "ix_company_code",
                table: "company",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customer_device_customer_no_device_no",
                table: "customer_device",
                columns: new[] { "customer_no", "device_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_doc_metadata_customer_name",
                table: "doc_metadata",
                column: "customer_name");

            migrationBuilder.CreateIndex(
                name: "ix_doc_metadata_project_no",
                table: "doc_metadata",
                column: "project_no");

            migrationBuilder.CreateIndex(
                name: "ix_document_kb_id_classification_parse_status",
                table: "document",
                columns: new[] { "kb_id", "classification", "parse_status" });

            migrationBuilder.CreateIndex(
                name: "ix_fault_case_case_no",
                table: "fault_case",
                column: "case_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fault_case_device_model_alarm_code",
                table: "fault_case",
                columns: new[] { "device_model", "alarm_code" });

            migrationBuilder.CreateIndex(
                name: "ix_fault_case_sync_status",
                table: "fault_case",
                column: "sync_status");

            migrationBuilder.CreateIndex(
                name: "ix_generation_session_template_id",
                table: "generation_session",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_base_company_id",
                table: "knowledge_base",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_base_tier_company_id",
                table: "knowledge_base",
                columns: new[] { "tier", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ix_parse_job_doc_id",
                table: "parse_job",
                column: "doc_id");

            migrationBuilder.CreateIndex(
                name: "ix_parse_job_status_queued_at",
                table: "parse_job",
                columns: new[] { "status", "queued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_project_customer_name_year_device_type",
                table: "project",
                columns: new[] { "customer_name", "year", "device_type" });

            migrationBuilder.CreateIndex(
                name: "ix_qa_message_session_id",
                table: "qa_message",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_qa_session_user_id_updated_at",
                table: "qa_session",
                columns: new[] { "user_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_role_code",
                table: "role",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_template_slot_template_id_tag",
                table: "template_slot",
                columns: new[] { "template_id", "tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_term_domain_zh",
                table: "term",
                columns: new[] { "domain", "zh" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_customer_no",
                table: "ticket",
                column: "customer_no");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_ticket_no",
                table: "ticket",
                column: "ticket_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_terminal_user_id_terminal_id",
                table: "user_terminal",
                columns: new[] { "user_id", "terminal_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vocab_term_vocab_key_value",
                table: "vocab_term",
                columns: new[] { "vocab_key", "value" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "backup_run");

            migrationBuilder.DropTable(
                name: "chunk");

            migrationBuilder.DropTable(
                name: "clause");

            migrationBuilder.DropTable(
                name: "customer_device");

            migrationBuilder.DropTable(
                name: "doc_metadata");

            migrationBuilder.DropTable(
                name: "fault_case");

            migrationBuilder.DropTable(
                name: "generation_session");

            migrationBuilder.DropTable(
                name: "parse_job");

            migrationBuilder.DropTable(
                name: "project");

            migrationBuilder.DropTable(
                name: "publish_record");

            migrationBuilder.DropTable(
                name: "qa_message");

            migrationBuilder.DropTable(
                name: "sync_log");

            migrationBuilder.DropTable(
                name: "sys_config");

            migrationBuilder.DropTable(
                name: "template_slot");

            migrationBuilder.DropTable(
                name: "term");

            migrationBuilder.DropTable(
                name: "ticket");

            migrationBuilder.DropTable(
                name: "user_terminal");

            migrationBuilder.DropTable(
                name: "vocab_term");

            migrationBuilder.DropTable(
                name: "document");

            migrationBuilder.DropTable(
                name: "qa_session");

            migrationBuilder.DropTable(
                name: "template");

            migrationBuilder.DropTable(
                name: "app_user");

            migrationBuilder.DropTable(
                name: "knowledge_base");

            migrationBuilder.DropTable(
                name: "role");

            migrationBuilder.DropTable(
                name: "company");
        }
    }
}
