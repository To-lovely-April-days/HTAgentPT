-- 同一 PostgreSQL 实例上给总部节点建第二个库。
-- 只在数据卷首次初始化时执行（docker-entrypoint-initdb.d 机制）；表结构由后端启动时的迁移自动创建。
-- 扩展在这里以超级用户身份预建：即便应用连接账号无超级权限（如换成外部托管库）迁移也能过。
CREATE DATABASE htagent_hq OWNER htagent;

\connect htagent
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

\connect htagent_hq
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
