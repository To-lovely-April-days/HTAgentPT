/** 尚未接入前端的功能位：后端接口已就绪，页面随后续前端批次落地（原型即规格）。 */
export default function Placeholder({ name, api }: { name: string; api: string }) {
  return (
    <div style={{ flexGrow: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
      <div className="card" style={{ padding: '20px 24px', maxWidth: 460 }}>
        <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 6 }}>「{name}」界面在后续前端批次接入</div>
        <div className="hint">
          服务端能力已就绪（{api}），高保真原型见 design/ 目录对应画布——原型即本页的实现规格。
        </div>
      </div>
    </div>
  );
}
