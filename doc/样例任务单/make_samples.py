# 由模板版任务单生成 10 份「已填好的历史任务单」样例（供语料入库与检索测试）
# 用法：python3 make_samples.py   （需要同目录下的模板版 docx）
import os, re, shutil, zipfile
import xml.etree.ElementTree as ET

TPL = 'XX订单号XX客户C类生产任务单-非标-模板版.docx'
OUT = 'samples'
W = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'
XMLNS_SPACE = '{http://www.w3.org/XML/1998/namespace}space'


def q(t):
    return f'{{{W}}}{t}'


def norm(s):
    return re.sub(r'\s+', '', s)


def txt(el):
    return ''.join(t.text or '' for t in el.iter(q('t')))


# ── 10 份样例的数据（43 个槽位全部填写；参数按容积/材质/压力档位拉开差异）──
S = [
 dict(contract_no='HT-2023-C091', order_date='2023-04-11', presales_person='张伟', customer_name='华东理工大学化学工程学院',
      urgency='一般', order_type='C类', sales_person='陈静', delivery_date='2023-06-15',
      device_name='磁力搅拌高压反应釜', device_qty='1', device_price='58000', device_model='CJF-5L',
      full_volume='5000', material='釜体及釜盖 316L，搅拌轴与桨叶 316L，密封面镀硬铬',
      structure_type='法兰', lifting='无', working_pressure='10', burst_pressure='15', design_pressure='12',
      working_temp='300', design_temp='350', viscosity_range='1～500', paddle_type='三叶后掠式桨', speed_range='0～1000',
      heating_type='电加热', inner_cooling='内置盘管冷却，接电磁阀与温控连锁',
      discharge='下展阀 DN15，带取样口', opening_lid='釜盖开口 6 处：进气口 M12、出气口 M12、测温套管 φ8、压力表口 M20、安全阀口 M20、备用口 M12',
      opening_body='无', power_supply='220V', footprint='台面占地不大于 600×500 mm',
      feed_end='气体经减压阀与单向阀进气，进气口带过滤器；液体由加料漏斗常压加入',
      back_end='出气口接冷凝回流管，冷凝液回流至釜内，尾气经背压阀排空',
      sampling='釜底取样阀取样，取样量不小于 5 ml', other_special='釜盖开启后需可整体移开，便于清洗',
      control_mode='标准按键', explosion_proof='无', interlock='超温超压自动断电并报警；冷却电磁阀随温度联动',
      accessory_name='聚四氟乙烯内衬杯', accessory_qty='2', accessory_spec='φ90×110 mm，配 5L 釜体',
      delivery_method='送货', pid_attached='无',
      producer='王建国', d_design='2023-04-20', d_purchase='2023-05-06', d_machining='2023-05-22',
      d_assembly='2023-06-02', d_electric='2023-06-06', d_debug='2023-06-09',
      review='参数齐全，按标准 5L 磁力釜工艺执行，交期可满足。',
      acc2=('石墨密封垫', '10', 'φ76×φ60×2 mm'), acc3=('', '', '')),

 dict(contract_no='HT-2025-C186', order_date='2025-03-06', presales_person='张伟', customer_name='华东理工大学化学工程学院',
      urgency='中度', order_type='B类', sales_person='陈静', delivery_date='2025-05-20',
      device_name='升降式高压反应釜', device_qty='1', device_price='96000', device_model='CJF-10L',
      full_volume='10000', material='釜体及釜盖 316L，内衬可拆卸 PTFE，法兰螺栓 35CrMoA',
      structure_type='快开', lifting='釜盖升降', working_pressure='10', burst_pressure='15', design_pressure='12',
      working_temp='300', design_temp='350', viscosity_range='1～2000', paddle_type='框式桨配底部刮壁', speed_range='0～800',
      heating_type='油浴控夹套', inner_cooling='内置 U 形管冷却，接电磁阀与温控连锁',
      discharge='下展阀 DN25，带保温夹套', opening_lid='釜盖开口 8 处：进气口 M12、出气口 M12、测温套管 φ8、压力表口 M20、安全阀口 M20、加料口 DN25、视镜 DN50、备用口 M12',
      opening_body='釜体侧壁 1 处视镜 DN50', power_supply='380V', footprint='不大于 900×700 mm，含升降立柱',
      feed_end='高压柱塞泵连续进料，进料口带止回阀与压力表',
      back_end='出气口接列管冷凝器与回收罐，回收罐带液位计',
      sampling='带高压取样阀，可在 10 MPa 工况下取样', other_special='釜盖升降行程不小于 300 mm，配限位与防坠',
      control_mode='新款触摸屏', explosion_proof='无', interlock='釜盖未锁紧禁止升温；超温超压自动断电并报警',
      accessory_name='导热油', accessory_qty='40', accessory_spec='YD-350 合成导热油，L',
      delivery_method='送货', pid_attached='有',
      producer='王建国', d_design='2025-03-18', d_purchase='2025-04-08', d_machining='2025-04-25',
      d_assembly='2025-05-08', d_electric='2025-05-12', d_debug='2025-05-15',
      review='升降结构按 B 类小非标执行，PID 图随单，触摸屏程序需单独排期。',
      acc2=('PTFE 内衬桶', '1', 'φ180×420 mm'), acc3=('高压取样阀', '1', 'DN6 / 25 MPa')),

 dict(contract_no='HT-2026-C001', order_date='2026-01-12', presales_person='李敏', customer_name='南方药业股份有限公司',
      urgency='紧急', order_type='C类', sales_person='刘洋', delivery_date='2026-03-05',
      device_name='防爆型磁力搅拌反应釜', device_qty='2', device_price='72000', device_model='CJF-3L',
      full_volume='3000', material='釜体及釜盖 哈氏合金 C276，搅拌轴 C276，密封件 全氟醚',
      structure_type='法兰', lifting='无', working_pressure='6', burst_pressure='9', design_pressure='7.5',
      working_temp='250', design_temp='300', viscosity_range='1～1000', paddle_type='涡轮桨双层', speed_range='0～1200',
      heating_type='电加热', inner_cooling='内置盘管冷却，C276 材质，接电磁阀连锁',
      discharge='球阀 DN10，C276 阀芯', opening_lid='釜盖开口 5 处：进气口 M12、出气口 M12、测温套管 φ6、压力表口 M20、安全阀口 M20',
      opening_body='无', power_supply='220V', footprint='单台不大于 500×450 mm，两台并排放置',
      feed_end='气体经减压阀进气，带质量流量计计量；液体由高压计量泵加入',
      back_end='尾气经冷阱后接活性炭吸附罐，防止腐蚀性气体外泄',
      sampling='釜底取样阀取样，取样管路 C276', other_special='接触介质部件一律 C276，禁用不锈钢紧固件',
      control_mode='触摸屏+电脑', explosion_proof='ExdⅡBT4', interlock='超温超压自动断电并切断进气；防爆区内电气须整体防爆',
      accessory_name='全氟醚密封圈', accessory_qty='20', accessory_spec='φ60×3.5 mm，耐 300 ℃',
      delivery_method='发货', pid_attached='有',
      producer='周强', d_design='2026-01-20', d_purchase='2026-02-02', d_machining='2026-02-14',
      d_assembly='2026-02-24', d_electric='2026-02-27', d_debug='2026-03-02',
      review='防爆等级与 C276 材质为强制要求，采购周期紧张，已提前锁定材料。',
      acc2=('C276 取样管', '2', 'φ6×1 mm，长 500 mm'), acc3=('防爆接线盒', '2', 'ExdⅡBT4')),

 dict(contract_no='HT-2026-C002', order_date='2026-02-03', presales_person='李敏', customer_name='南方药业股份有限公司',
      urgency='一般', order_type='C类', sales_person='刘洋', delivery_date='2026-04-10',
      device_name='双联平行高压反应釜', device_qty='1', device_price='86000', device_model='CJF-3L-2',
      full_volume='3000', material='釜体及釜盖 316L，内衬 PTFE，密封件 氟橡胶',
      structure_type='快开', lifting='无', working_pressure='5', burst_pressure='7.5', design_pressure='6.3',
      working_temp='200', design_temp='250', viscosity_range='1～800', paddle_type='锚式桨', speed_range='0～600',
      heating_type='油浴控物料', inner_cooling='无内置冷却，夹套通冷却水',
      discharge='球阀 DN10，两釜各一', opening_lid='每釜釜盖开口 4 处：进气口 M12、出气口 M12、测温套管 φ6、安全阀口 M20',
      opening_body='无', power_supply='220V', footprint='不大于 800×450 mm，双釜共底座',
      feed_end='两釜独立进气，共用一路减压阀，各带针阀调节',
      back_end='两釜出气分别接冷凝管后汇入同一尾气管',
      sampling='无', other_special='两釜温度与转速需可独立设定，便于平行对比实验',
      control_mode='老款触摸屏', explosion_proof='无', interlock='任一釜超温超压均自动断电并报警',
      accessory_name='PTFE 内衬杯', accessory_qty='4', accessory_spec='φ75×100 mm，配 3L 釜体',
      delivery_method='发货', pid_attached='无',
      producer='周强', d_design='2026-02-12', d_purchase='2026-02-28', d_machining='2026-03-16',
      d_assembly='2026-03-28', d_electric='2026-04-01', d_debug='2026-04-06',
      review='平行釜控温独立，电气按双回路设计，其余沿用标准 3L 釜工艺。',
      acc2=('氟橡胶密封圈', '20', 'φ60×3.5 mm'), acc3=('', '', '')),

 dict(contract_no='HT-2025-A233', order_date='2025-06-18', presales_person='王强', customer_name='江苏永诚化工有限公司',
      urgency='中度', order_type='A类', sales_person='赵磊', delivery_date='2025-09-12',
      device_name='一键快开升降式高压反应釜', device_qty='1', device_price='185000', device_model='CJF-20L',
      full_volume='20000', material='釜体及釜盖 316L，夹套 Q345R，接触介质面抛光至 Ra0.4',
      structure_type='一键快开', lifting='釜体升降', working_pressure='8', burst_pressure='12', design_pressure='10',
      working_temp='300', design_temp='350', viscosity_range='1～5000', paddle_type='框式桨配锚式刮壁', speed_range='0～400',
      heating_type='油浴控夹套', inner_cooling='内置 U 形管冷却，接电磁阀与温控连锁',
      discharge='下展阀 DN40，带夹套保温与放净口',
      opening_lid='釜盖开口 9 处：进气口 DN15、出气口 DN15、测温套管 φ10、压力表口 M20、安全阀口 DN25、加料口 DN40、视镜 DN80、照明口 DN25、备用口 M20',
      opening_body='釜体下部 1 处放净口 DN15', power_supply='380V', footprint='不大于 1600×1200 mm，含升降机构与油浴槽',
      feed_end='固体由 DN40 加料口人工投料，液体由计量泵经缓冲罐进料',
      back_end='出气接列管冷凝器、回流分配器与回收罐，可切换回流与采出',
      sampling='带高压取样阀与取样冷却器，取样量 10～50 ml',
      other_special='属压力容器，须提供第三方检验报告与铭牌；焊缝 100% 探伤',
      control_mode='电脑', explosion_proof='ExdⅡCT4', interlock='升降到位方可升温；超温超压自动断电、切气并报警；搅拌过载停机保护',
      accessory_name='导热油', accessory_qty='120', accessory_spec='YD-350 合成导热油，L',
      delivery_method='送货', pid_attached='有',
      producer='王建国', d_design='2025-07-02', d_purchase='2025-07-25', d_machining='2025-08-15',
      d_assembly='2025-08-29', d_electric='2025-09-03', d_debug='2025-09-08',
      review='A 类压力容器，设计与探伤资料须随机交付，油浴槽单独订制。',
      acc2=('压力容器检验报告', '1', '第三方出具，含铭牌'), acc3=('取样冷却器', '1', '不锈钢盘管式')),

 dict(contract_no='HT-2024-C118', order_date='2024-05-09', presales_person='张伟', customer_name='皖江新材料研究院',
      urgency='一般', order_type='C类', sales_person='陈静', delivery_date='2024-07-01',
      device_name='微型高压反应釜', device_qty='4', device_price='32000', device_model='CJF-1L',
      full_volume='1000', material='釜体及釜盖 钛材 TA2，搅拌轴 TA2，密封件 全氟醚',
      structure_type='法兰', lifting='无', working_pressure='15', burst_pressure='22.5', design_pressure='18',
      working_temp='350', design_temp='400', viscosity_range='1～200', paddle_type='两叶平桨', speed_range='0～1500',
      heating_type='电加热', inner_cooling='内置盘管冷却，TA2 材质',
      discharge='堵头，无下放料', opening_lid='釜盖开口 4 处：进气口 M12、出气口 M12、测温套管 φ6、安全阀口 M16',
      opening_body='无', power_supply='220V', footprint='单台不大于 400×350 mm，四台共用一张实验台',
      feed_end='气体经减压阀与单向阀进气；液体开盖加入',
      back_end='出气口接背压阀后排空，不做回收', sampling='无',
      other_special='四台参数一致，需可单独控温；钛材禁与铁质工具接触',
      control_mode='标准按键', explosion_proof='无', interlock='超温超压自动断电并报警',
      accessory_name='钛材密封垫', accessory_qty='40', accessory_spec='φ50×φ38×2 mm，TA2',
      delivery_method='发货', pid_attached='无',
      producer='周强', d_design='2024-05-17', d_purchase='2024-06-03', d_machining='2024-06-14',
      d_assembly='2024-06-21', d_electric='2024-06-24', d_debug='2024-06-27',
      review='钛材备料周期较长，四台同批加工，控温模块按四路配置。',
      acc2=('', '', ''), acc3=('', '', '')),

 dict(contract_no='HT-2025-A301', order_date='2025-08-22', presales_person='王强', customer_name='广州瑞康制药有限公司',
      urgency='紧急', order_type='A类', sales_person='赵磊', delivery_date='2025-11-28',
      device_name='防爆型快开式高压反应釜', device_qty='1', device_price='268000', device_model='CJF-50L',
      full_volume='50000', material='釜体及釜盖 316L，接触介质面整体 PTFE 内衬，夹套 Q345R',
      structure_type='一键快开', lifting='釜盖升降', working_pressure='6', burst_pressure='9', design_pressure='7.5',
      working_temp='250', design_temp='300', viscosity_range='10～10000', paddle_type='框式桨配双层刮壁', speed_range='0～300',
      heating_type='油浴控夹套', inner_cooling='内置 U 形管冷却，PTFE 包覆，接电磁阀连锁',
      discharge='下展阀 DN50，PTFE 内衬，带放净与冲洗口',
      opening_lid='釜盖开口 10 处：进气口 DN15、出气口 DN25、测温套管 φ10、压力表口 M20、安全阀口 DN25、加料口 DN50、视镜 DN100、照明口 DN25、CIP 喷淋口 DN15、备用口 M20',
      opening_body='釜体下部放净口 DN25，侧壁视镜 DN80 各 1 处',
      power_supply='380V', footprint='不大于 2000×1500 mm，含升降与油浴系统',
      feed_end='粉料经真空上料机由 DN50 口投入，液体由计量泵经流量计进料',
      back_end='出气经列管冷凝器、气液分离罐后接真空系统，可做减压蒸馏',
      sampling='无菌取样阀取样，取样口可蒸汽灭菌',
      other_special='按 GMP 要求，内表面抛光至 Ra0.4 并提供材质证明；属压力容器，须第三方检验',
      control_mode='触摸屏+电脑', explosion_proof='ExdⅡBT4', interlock='釜盖未锁紧禁止升温与加压；超温超压自动断电、切气并报警；CIP 过程禁止搅拌启动',
      accessory_name='导热油', accessory_qty='260', accessory_spec='YD-300 合成导热油，L',
      delivery_method='送货', pid_attached='有',
      producer='王建国', d_design='2025-09-05', d_purchase='2025-10-08', d_machining='2025-10-31',
      d_assembly='2025-11-14', d_electric='2025-11-19', d_debug='2025-11-25',
      review='GMP 与防爆双重要求，内衬与抛光为验收关键项，资料随机交付。',
      acc2=('无菌取样阀', '2', 'DN10，可蒸汽灭菌'), acc3=('PTFE 喷淋球', '1', 'DN15，CIP 用')),

 dict(contract_no='HT-2024-A176', order_date='2024-09-14', presales_person='王强', customer_name='山东鲁盛精细化工有限公司',
      urgency='中度', order_type='A类', sales_person='刘洋', delivery_date='2024-12-20',
      device_name='大容积升降式反应釜', device_qty='1', device_price='352000', device_model='CJF-100L',
      full_volume='100000', material='釜体及釜盖 904L，夹套 Q345R，搅拌轴 904L',
      structure_type='法兰', lifting='釜体升降', working_pressure='3', burst_pressure='4.5', design_pressure='3.8',
      working_temp='200', design_temp='250', viscosity_range='100～20000', paddle_type='双层框式桨配底部刮壁', speed_range='0～200',
      heating_type='油浴控夹套', inner_cooling='夹套冷却为主，内置盘管辅助，接电磁阀连锁',
      discharge='下展阀 DN80，带夹套保温',
      opening_lid='釜盖开口 8 处：进气口 DN25、出气口 DN40、测温套管 φ12、压力表口 M20、安全阀口 DN40、加料口 DN80、视镜 DN100、照明口 DN25',
      opening_body='釜体下部放净口 DN25', power_supply='380V', footprint='不大于 2600×1800 mm，设备总高不超过 2800 mm',
      feed_end='液体由离心泵经流量计进料，固体由 DN80 加料口经斗提投入',
      back_end='出气经冷凝器与缓冲罐后接尾气吸收塔',
      sampling='釜底取样阀取样，带冷却夹套',
      other_special='904L 焊接须由持证焊工施焊并提供焊接工艺评定；属压力容器，须第三方检验',
      control_mode='分体式', explosion_proof='ExdⅡBT4', interlock='升降到位方可运行；超温超压自动断电并报警；搅拌电机过载保护',
      accessory_name='导热油', accessory_qty='420', accessory_spec='YD-300 合成导热油，L',
      delivery_method='送货', pid_attached='有',
      producer='周强', d_design='2024-09-29', d_purchase='2024-10-26', d_machining='2024-11-20',
      d_assembly='2024-12-06', d_electric='2024-12-11', d_debug='2024-12-17',
      review='904L 材料与焊接资质为关键路径，控制柜按分体式落地安装。',
      acc2=('焊接工艺评定报告', '1', '含焊工资质复印件'), acc3=('压力容器检验报告', '1', '第三方出具，含铭牌')),

 dict(contract_no='HT-2023-C205', order_date='2023-11-07', presales_person='张伟', customer_name='中原石化研究院',
      urgency='一般', order_type='C类', sales_person='陈静', delivery_date='2024-01-18',
      device_name='超高压微型反应釜', device_qty='2', device_price='68000', device_model='CJF-500ML',
      full_volume='500', material='釜体及釜盖 哈氏合金 C276，密封件 全氟醚，紧固件 GH4169',
      structure_type='快开', lifting='无', working_pressure='20', burst_pressure='30', design_pressure='25',
      working_temp='400', design_temp='450', viscosity_range='1～100', paddle_type='磁力驱动两叶桨', speed_range='0～2000',
      heating_type='电加热', inner_cooling='外置风冷 + 内置盘管快速降温',
      discharge='堵头，无下放料',
      opening_lid='釜盖开口 5 处：进气口 M10、出气口 M10、测温套管 φ3、压力表口 M16、爆破片口 M16',
      opening_body='无', power_supply='220V', footprint='单台不大于 350×300 mm',
      feed_end='高压气瓶经二级减压阀与单向阀进气，最高进气压力 25 MPa',
      back_end='出气经背压阀与冷阱后排空，配气体采样袋接口',
      sampling='无', other_special='配爆破片安全装置，爆破压力 30 MPa；升温速率不低于 10 ℃/min',
      control_mode='新款触摸屏', explosion_proof='无', interlock='超温超压自动断电、切断进气并声光报警',
      accessory_name='爆破片', accessory_qty='10', accessory_spec='30 MPa，φ16，C276',
      delivery_method='发货', pid_attached='无',
      producer='周强', d_design='2023-11-16', d_purchase='2023-12-05', d_machining='2023-12-22',
      d_assembly='2024-01-05', d_electric='2024-01-09', d_debug='2024-01-15',
      review='超高压等级，密封与爆破片为关键件，出厂须做 1.25 倍水压试验。',
      acc2=('C276 密封垫', '20', 'φ36×φ26×2 mm'), acc3=('', '', '')),

 dict(contract_no='HT-2026-D014', order_date='2026-03-02', presales_person='李敏', customer_name='深圳凯特新能源科技有限公司',
      urgency='中度', order_type='D类', sales_person='赵磊', delivery_date='2026-04-22',
      device_name='高压光化学反应釜（旧机改造）', device_qty='1', device_price='45000', device_model='GHX-2L',
      full_volume='2000', material='釜体 316L，石英套管 GE214，密封件 氟橡胶',
      structure_type='快开', lifting='无', working_pressure='1', burst_pressure='1.5', design_pressure='1.3',
      working_temp='150', design_temp='200', viscosity_range='1～300', paddle_type='磁力搅拌子配导流筒', speed_range='0～1500',
      heating_type='电加热', inner_cooling='夹套通循环冷却水，控温 5～150 ℃',
      discharge='球阀 DN10',
      opening_lid='釜盖开口 5 处：光源石英套管 φ40、进气口 M12、出气口 M12、测温套管 φ6、安全阀口 M16',
      opening_body='无', power_supply='220V', footprint='不大于 700×600 mm，含光源电源箱',
      feed_end='气体经质量流量计进气，可通氮气或氧气',
      back_end='出气接冷凝管后排空，出口带在线取样接口',
      sampling='侧壁取样阀在线取样，不停机', other_special='在原 2L 釜体上改造：新增光源接口、石英套管与遮光罩，原搅拌与控温系统保留',
      control_mode='标准按键', explosion_proof='无', interlock='遮光罩未闭合禁止点亮光源；超温自动断电并报警',
      accessory_name='汞灯光源', accessory_qty='1', accessory_spec='500W，配镇流器与石英冷阱',
      delivery_method='发货', pid_attached='无',
      producer='王建国', d_design='2026-03-09', d_purchase='2026-03-23', d_machining='2026-04-03',
      d_assembly='2026-04-13', d_electric='2026-04-16', d_debug='2026-04-20',
      review='售后改造件，原釜体经水压复验合格后方可改造，光源安全连锁为验收项。',
      acc2=('石英套管', '2', 'φ40×280 mm，GE214'), acc3=('遮光罩', '1', '铝合金，带观察窗')),
]

# 非槽位的空白格（生产阶段填写项）：按标签写入右侧单元格
STAGE = [('生产人员', 'producer'), ('设计完成日期', 'd_design'), ('采购完成日期', 'd_purchase'),
         ('加工完成日期', 'd_machining'), ('装配完成日期', 'd_assembly'), ('电器完成日期', 'd_electric'),
         ('调试完成日期', 'd_debug')]


def set_cell_text(tc, value):
    """整格改写为一行纯文本（保留段落属性）。"""
    ps = tc.findall(q('p'))
    if not ps:
        ps = [ET.SubElement(tc, q('p'))]
    for k, p in enumerate(ps):
        for child in list(p):
            if child.tag != q('pPr'):
                p.remove(child)
        if k == 0:
            r = ET.SubElement(p, q('r'))
            t = ET.SubElement(r, q('t'))
            t.text = value
            t.set(XMLNS_SPACE, 'preserve')


def build(data, path):
    tree = ET.parse('src2/word/document.xml')
    root = tree.getroot()

    # 1) 槽位：把内容控件里的占位文字换成取值（保留控件，便于回看对应关系）
    filled = set()
    for sdt in root.iter(q('sdt')):
        pr = sdt.find(q('sdtPr'))
        if pr is None:
            continue
        tag_el = pr.find(q('tag'))
        if tag_el is None:
            continue
        tag = tag_el.get(q('val'))
        if tag not in data:
            continue
        content = sdt.find(q('sdtContent'))
        if content is None:
            continue
        runs = list(content.iter(q('r')))
        if not runs:
            r = ET.SubElement(content, q('r'))
            runs = [r]
        for i, r in enumerate(runs):
            for t in r.findall(q('t')):
                r.remove(t)
            if i == 0:
                t = ET.SubElement(r, q('t'))
                t.text = data[tag]
                t.set(XMLNS_SPACE, 'preserve')
        filled.add(tag)

    # 2) 生产阶段栏与审核意见（非槽位）
    for tr in root.iter(q('tr')):
        tcs = tr.findall(q('tc'))
        for i, tc in enumerate(tcs):
            label = norm(txt(tc))
            for name, key in STAGE:
                if label == name and i + 1 < len(tcs) and norm(txt(tcs[i + 1])) == '':
                    set_cell_text(tcs[i + 1], data[key])
            if label.startswith('审核意见'):
                set_cell_text(tc, '审核意见：' + data['review'])

    # 3) 配件清单第 2、3 行
    for tbl in root.iter(q('tbl')):
        trs = tbl.findall(q('tr'))
        for k, tr in enumerate(trs):
            heads = [norm(txt(tc)) for tc in tr.findall(q('tc'))]
            if all(h in heads for h in ['名称', '数量', '规格']) and not any('单价' in h for h in heads):
                for offset, key in ((2, 'acc2'), (3, 'acc3')):
                    if k + offset >= len(trs):
                        continue
                    name, qty, spec = data[key]
                    if not name:
                        continue
                    cells = trs[k + offset].findall(q('tc'))
                    if len(cells) >= 4:
                        set_cell_text(cells[1], name)
                        set_cell_text(cells[2], qty)
                        set_cell_text(cells[3], spec)

    tree.write('src2/word/document.xml', xml_declaration=True, encoding='UTF-8', method='xml')
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED) as z:
        for base, _, files in os.walk('src2'):
            for f in files:
                full = os.path.join(base, f)
                z.write(full, os.path.relpath(full, 'src2'))
    return filled


os.makedirs(OUT, exist_ok=True)
slot_total = None
for i, data in enumerate(S, 1):
    if os.path.isdir('src2'):
        shutil.rmtree('src2')
    with zipfile.ZipFile(TPL) as z:
        z.extractall('src2')
    name = f"{i:02d}-{data['contract_no']}-{data['customer_name'][:6]}-{data['device_model']}-生产任务单.docx"
    filled = build(data, os.path.join(OUT, name))
    slot_total = len(filled) if slot_total is None else slot_total
    assert len(filled) == 43, f"{name} 只填了 {len(filled)} 项"
    print(f"{name}  ({len(filled)} 项)")
print('完成：', len(S), '份，输出目录', OUT)
