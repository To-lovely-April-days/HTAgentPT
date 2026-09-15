// 构建期用词边界检查（设计规范）：客户侧不出现内部概念词。命中即构建失败。
import fs from 'node:fs';
import path from 'node:path';

const FORBIDDEN = ['知识库', '密级', '私有库', '共享库', '工作台', '管理员', '审计', '解析', '分块', '召回', '向量', '阈值'];
const dist = path.resolve('dist');
let bad = [];
for (const file of fs.readdirSync(path.join(dist, 'assets'))) {
  const text = fs.readFileSync(path.join(dist, 'assets', file), 'utf-8');
  for (const w of FORBIDDEN) if (text.includes(w)) bad.push(`${file}: ${w}`);
}
if (bad.length) {
  console.error('客户侧构建产物中出现内部概念词，构建失败：');
  for (const b of bad) console.error('  ' + b);
  process.exit(1);
}
console.log('用词边界检查通过：构建产物不含内部概念词。');
