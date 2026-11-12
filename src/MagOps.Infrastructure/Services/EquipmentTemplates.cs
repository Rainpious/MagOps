using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed record EquipmentChecklistTemplate(
    string TemplateId,
    string Title,
    IReadOnlyList<string> Aliases,
    string RequirementNote,
    IReadOnlyList<EquipmentChecklistItem> Items);

public static class EquipmentTemplates
{
    public const string Required = "REQUIRED";
    public const string Optional = "OPTIONAL";

    private static EquipmentChecklistItem Item(
        string id,
        string section,
        string category,
        string name,
        string expected,
        string requirement,
        string check,
        string source = "",
        bool quantityEditable = false,
        int defaultQuantity = 0,
        int minQuantity = 0,
        string quantityUnit = "",
        bool custom = false) =>
        new()
        {
            ItemId = id,
            Section = section,
            Category = category,
            Name = name,
            Expected = expected,
            Requirement = requirement,
            Check = check,
            SourceAttribute = source,
            QuantityEditable = quantityEditable,
            DefaultQuantity = defaultQuantity,
            MinQuantity = minQuantity,
            QuantityUnit = quantityUnit,
            IsCustom = custom
        };

    private static readonly EquipmentChecklistTemplate Dji = new(
        "dji-matrice-400",
        "DJI Matrice 400 出发设备清点",
        [
            "DJI Matrice 400",
            "DJI Matrice 400 飞行器",
            "Matrice 400",
            "M400",
            "DJ400"
        ],
        "v0.5.5 官方文档核验版：19 项，其中 12 项必备、7 项可选。"
        + "物品构成和检查内容参考 DJI 官方组合物品清单、Safety Guidelines 与 FAQ；"
        + "必备/可选和项目携带数量是当前项目管理规则，并非 DJI 官方装箱等级。"
        + "航磁仪器金属架仅初次安装时从光泵箱取出，之后只随飞行器检查固定状态，不设独立清点项。",
        [
            Item(
                "m400_case",
                "无人机箱内组件",
                "运输箱",
                "DJI Matrice 400 运输箱（Carrying Case）",
                "1 个",
                Required,
                "检查箱体、锁扣、提手/轮组、内衬和槽位；确认机体、遥控、电池及常用附件均有对应位置，运输前无松动物。",
                "DJI 官方组合物品清单"),

            Item(
                "aircraft",
                "无人机箱内组件",
                "飞行器",
                "DJI Matrice 400 飞行器",
                "1 台",
                Required,
                "检查机体、机臂、锁止机构、接口和可见结构；机内无异物，通风/散热孔无遮挡；"
                + "机臂展开并锁定，载荷安装牢靠无遮挡；视觉系统、红外传感器、辅助灯与 LiDAR 清洁无遮挡。"
                + "航磁仪器金属架仅首次安装时从光泵箱取出，之后只检查其在无人机上的固定状态，不再单独清点。",
                "DJI Safety Guidelines + 用户实物规则"),

            Item(
                "landing_gear",
                "无人机箱内组件",
                "起落架",
                "起落架",
                "2 个",
                Required,
                "2 个齐全；检查连接位、套筒/紧固处、杆体和脚垫无裂纹、变形或松动；起飞前确认安装牢固。",
                "DJI 飞前检查 + 项目拆分项"),

            Item(
                "m400_2510f_propellers",
                "无人机箱内组件",
                "螺旋桨",
                "DJI Matrice 400 2510F 螺旋桨",
                "1 对/套（CCW×1、CW×1）",
                Required,
                "核对 2510F 型号，不与其他机型桨叶混用；检查全部在用桨叶/桨座无老化、缺口、裂纹、变形、松动，安装牢固；"
                + "损坏桨叶及时更换。随箱备用对按 CCW×1、CW×1 清点。",
                "DJI 2510F Propeller + Safety Guidelines"),

            Item(
                "rc_plus_2",
                "无人机箱内组件",
                "遥控器",
                "DJI RC Plus 2 行业增强版遥控器",
                "1 台",
                Required,
                "电量充足；屏幕、按键、摇杆、接口正常；天线位置正确；与飞行器完成链路连接并确认遥控权；"
                + "DJI Pilot 2 与设备固件处于已核准/最新状态。",
                "DJI 官方组合物品清单 + Safety Guidelines"),

            Item(
                "rc_harness",
                "无人机箱内组件",
                "遥控器背带/支架",
                "DJI RC Plus 2 背带及支架组件",
                "1 套",
                Optional,
                "背带、支架及连接件齐全；织带、扣具、固定点无破损，安装后不松脱。",
                "DJI 官方组合物品清单"),

            Item(
                "sub2g_sdr",
                "无人机箱内组件",
                "遥控通信模块",
                "DJI RC Plus 2 sub2G SDR 模块",
                "1 个*",
                Optional,
                "如任务使用则确认模块安装牢固、触点/接口正常并完成链路测试；使用频段必须符合当地无线电规定。",
                "DJI 官方组合物品清单 / FAQ"),

            Item(
                "front_prop_holder",
                "无人机箱内组件",
                "运输固定器",
                "前桨固定器",
                "1 个",
                Required,
                "运输/收纳时确认在位且无裂纹、变形；起飞前确认已从桨叶上取下，避免妨碍桨叶展开。",
                "DJI 官方组合物品清单 + 飞前逻辑"),

            Item(
                "rear_prop_holder",
                "无人机箱内组件",
                "运输固定器",
                "后桨固定器",
                "1 个",
                Required,
                "运输/收纳时确认在位且无裂纹、变形；起飞前确认已从桨叶上取下，避免妨碍桨叶展开。",
                "DJI 官方组合物品清单 + 飞前逻辑"),

            Item(
                "gimbal_damper",
                "无人机箱内组件",
                "云台减震件",
                "DJI Matrice 400 Gimbal Damper",
                "1 项/套",
                Optional,
                "检查减震件无老化、裂纹、撕裂、脱落或明显变形；安装位正确、受力均匀。",
                "DJI 官方组合物品清单"),

            Item(
                "lidar_cover",
                "无人机箱内组件",
                "LiDAR 保护罩",
                "旋转 LiDAR 校准/保护罩",
                "1 个",
                Required,
                "旋转 LiDAR 不使用/运输时应装好保护罩；罩体无破损、无遗失。"
                + "飞行前确认保护罩已取下，并确认 LiDAR 视窗清洁、无遮挡。",
                "DJI 官方组合物品清单 + FAQ + Safety Guidelines"),

            Item(
                "eport_caps",
                "无人机箱内组件",
                "接口保护盖",
                "E-Port V2 接口橡胶保护盖",
                "2 个",
                Optional,
                "2 个齐全、无破裂或变形；未使用的接口应正确盖合并保持清洁，使用载荷接口时确认保护盖妥善收纳。",
                "DJI 官方组合物品清单 + Safety Guidelines"),

            Item(
                "rubber_cap_screws",
                "无人机箱内组件",
                "保护盖紧固件",
                "Rubber Plug Screws / 橡胶塞固定螺钉",
                "4 个",
                Optional,
                "4 个齐全；检查螺纹、螺钉头无滑牙、锈蚀或变形，并与对应接口保护件配套。",
                "DJI 官方组合物品清单"),

            Item(
                "usb_c_cable",
                "无人机箱内组件",
                "USB-C 数据线",
                "USB-C 至 USB-C 数据线",
                "1 根",
                Optional,
                "线缆与两端接口无破损、弯折或松动；连接遥控器/电脑进行数据或升级时可正常识别。",
                "DJI 官方组合物品清单"),

            // Keep the historical ItemId to preserve already-saved checklist
            // state, but use DJI's official package naming.
            Item(
                "screwdriver",
                "无人机箱内组件",
                "维修工具",
                "维修工具套装（Screws and Tools）",
                "1 套",
                Required,
                "核对随箱维修工具及紧固件齐全；刀头/工具无明显磨损，螺钉无滑牙、锈蚀或变形；使用后归位。",
                "DJI 官方组合物品清单"),

            Item(
                "tb100",
                "电池与电池箱组件",
                "飞行电池",
                "TB100 智能飞行电池",
                "项目默认 3 块（DJI 标准组合列 1 块）",
                Required,
                "填写实际携带数量；逐块检查电量、外壳、接口/触点，无鼓包、渗漏或损伤；装机后确认锁止可靠。"
                + "项目默认携带 3 块为作业配置，不是 DJI 标准装箱数量。",
                "DJI 官方组合物品清单 + 项目配置",
                true,
                3,
                1,
                "块"),

            Item(
                "wb37",
                "电池与电池箱组件",
                "遥控器电池",
                "WB37 智能电池",
                "1 块",
                Required,
                "检查电量、外壳和触点；装入遥控器/充电设备后确认锁止和供电正常。",
                "DJI 官方组合物品清单",
                true,
                1,
                1,
                "块"),

            Item(
                "bs100",
                "电池与电池箱组件",
                "智能电池箱",
                "BS100 智能电池箱",
                "1 台",
                Required,
                "检查箱体、接口、轮组/提手和供电线缆；上电后确认 TB100/WB37 充电接口状态正常，并核对本地电源规格。"
                + "电源线按项目实物作为 BS100 附属部件清点，不再单独设条目。",
                "DJI 官方组合物品清单 / FAQ + 项目实物",
                true,
                1,
                1,
                "台"),

            Item(
                "cleaning",
                "工具与备件",
                "清洁工具",
                "清洁工具套装（Cleaning Tools）",
                "1 套",
                Optional,
                "清洁布/刷具等齐全、洁净；用于视觉系统、红外窗口、LiDAR 等光学/传感区域时避免刮伤或留下污染。",
                "DJI 官方组合物品清单 + Safety Guidelines")
        ]);

    private static readonly EquipmentChecklistTemplate Gsm = new(
        "gsm-19t",
        "GSM-19T 出发设备清点",
        [
            "GSM-19T",
            "GSM 19T",
            "19T"
        ],
        "v0.6.4 现场清点修正版：11 项，其中 8 项必备、3 项可选。"
        + "依据 GEM Systems GSM-19T 产品资料和 Capabilities 核验。"
        + "官方资料中的 12 V 2.2 Ah 为仪器内置电池型号/供电说明，不作为需要额外装箱清点的独立物品；"
        + "电池状态并入主机开机与充电检查。",
        [
            Item(
                "case",
                "仪器箱内组件",
                "仪器箱",
                "Shipping / Carrying Case 仪器箱",
                "1 只",
                Required,
                "检查 Shipping Case 箱体、锁扣、提手、内衬和槽位；标准组件应能按固定位置装箱。",
                "GEM Systems Standard Components"),

            Item(
                "console",
                "仪器箱内组件",
                "主机",
                "GSM-19T Console 主机",
                "1 台",
                Required,
                "检查 LCD/键盘、时间日期、内存和 RS-232 接口；开机后确认读数、信号质量指示及梯度警告功能可正常显示；"
                + "同时确认内置电池充电状态、续航表现和低电压告警正常。",
                "GEM Systems GSM-19T brochure"),

            Item(
                "sensor",
                "仪器箱内组件",
                "质子传感器",
                "Standard Proton Sensor 质子传感器",
                "1 只",
                Required,
                "外壳、接头无裂纹/松动/氧化；与主机连接后能稳定读数，并结合信号质量/梯度警告判断测量状态。",
                "GEM Systems GSM-19T brochure"),

            Item(
                "sensor_cable",
                "仪器箱内组件",
                "传感器线",
                "1.2 m Sensor Cable & Connector",
                "1 根（官方 1.2 m）",
                Required,
                "检查 1.2 m 传感器线无破皮、折伤或硬化；接头针脚完整、锁紧可靠；接机测试通讯/读数。",
                "GEM Systems Capabilities"),

            Item(
                "staff",
                "仪器箱内组件",
                "测杆",
                "Sectional Staff 分节式传感器杆",
                "4 节；组装全长约 2240 mm",
                Required,
                "各节杆体无弯曲、裂纹；螺纹/插接处正常；组装后总长度约 2240 mm，传感器固定可靠。",
                "GEM Systems Capabilities + 项目实物"),

            Item(
                "harness",
                "仪器箱内组件",
                "主机背带",
                "Harness / Carrying Straps 背带",
                "1 套",
                Optional,
                "官方标准组件之一；检查织带、扣具、缝线和主机固定点，佩戴后主机无明显松脱。",
                "GEM Systems Standard Components"),

            Item(
                "charger",
                "电源与充电",
                "充电器",
                "Battery Charger 充电器",
                "1 套",
                Required,
                "官方标准组件；检查充电器、输入电源线和输出接口，连接仪器电池后能正常充电。",
                "GEM Systems Standard Components"),

            Item(
                "rs232",
                "数据导出与通讯",
                "通讯",
                "RS-232 数据线",
                "1 根",
                Required,
                "官方标准组件；检查 6 针防水接口/数据线状态，与主机和电脑链路测试可正常传输。",
                "GEM Systems brochure"),

            Item(
                "rs232_usb",
                "数据导出与通讯",
                "通讯",
                "RS-232 → USB Adapter",
                "1 只",
                Optional,
                "官方标准组件；检查 USB 转换器和驱动/COM 口识别，能与 RS-232 数据线配合下载数据。",
                "GEM Systems brochure"),

            Item(
                "gemlink",
                "数据导出与通讯",
                "软件",
                "GEMLink+ 软件",
                "1 套",
                Required,
                "官方标准组件；野外电脑可启动并识别数据文件，能够完成下载、快速 QA/QC/绘图等基础检查。",
                "GEM Systems brochure"),

            Item(
                "manual",
                "资料",
                "资料",
                "Instruction Manual / 使用说明",
                "1 份/电子版",
                Optional,
                "官方标准组件；确认电子版或纸质版可查，版本与当前仪器软件/固件尽量一致。",
                "GEM Systems Standard Components")
        ]);

    private static readonly EquipmentChecklistTemplate GpamM350Rm = new(
        "gpam-m350-rm",
        "GPAM-M350-RM 航磁设备出发清点",
        [
            "GPAM-M350-RM",
            "GPAM M350 RM"
        ],
        "v0.5.5 实物清单 + 厂商族系资料核验版：17 项，其中 8 项必备、9 项可选。"
        + "未检索到 GPAM-M350-RM 的公开精确装箱清单，因此保留用户已确认的 17 项实物附件。"
        + "仅用同厂 GeophysicsPlus GPAM-M300-RM/GPAM-RM 公开资料校核探头与数据采集器的功能性检查；"
        + "精确型号、供电和附件参数以本机铭牌/实物为准。",
        [
            Item(
                "gpam_probe_assembly",
                "探头与结构件",
                "磁力仪探头/航磁探杆",
                "航磁光泵探头总成",
                "1套",
                Required,
                "核对探头铭牌/型号并检查碳纤维探杆、探头端、铰链、固定螺钉和连接线；"
                + "航空插头针脚、锁紧环完好。厂商公开 GPAM 同系列采用铷原子光泵传感器，"
                + "具体传感器型号/参数以本机铭牌为准。",
                "用户实物 + GeophysicsPlus 同系列资料",
                true,
                1,
                1,
                "套"),

            Item(
                "gpam_carbon_extension",
                "探头与结构件",
                "碳纤维杆",
                "碳纤维延长/支撑杆",
                "1根",
                Required,
                "检查杆体裂纹、端部接头、螺纹及锁紧机构。",
                "用户实物",
                true,
                1,
                1,
                "根"),

            Item(
                "gpam_data_acquisition_box",
                "采集与通信",
                "数据盒子",
                "数据采集器",
                "1台",
                Required,
                "记录铭牌/编号；检查供电、SD/数据存储、GPS/北斗定位、无线传输、姿态数据、时间同步及数据写入。"
                + "厂商公开同系列资料给出的电压/存储容量仅用于功能校核，精确参数以本机铭牌为准。",
                "用户实物 + GeophysicsPlus 同系列资料",
                true,
                1,
                1,
                "台"),

            Item(
                "gpam_bluetooth_terminal",
                "采集与通信",
                "蓝牙盒、无线终端",
                "蓝牙无线终端/地面通信盒",
                "1台*",
                Required,
                "按项目实物清点天线、USB-C、电源键和电池；通电后与数据采集器完成配对/通信测试。"
                + "公开同系列资料仅确认系统具有无线传输能力，当前终端具体型号以实物为准。",
                "用户实物；厂商公开资料未列精确 M350-RM 装箱项",
                true,
                1,
                1,
                "台*"),

            Item(
                "gpam_safe_battery",
                "电源与充电",
                "可充电锂电池",
                "安全锂电池组",
                "2块",
                Optional,
                "两块分别检查鼓包、破损、端子和实际电压/容量标识；分别测试充放电与供电稳定性。"
                + "精确电气参数以电池铭牌为准。",
                "用户实物",
                true,
                2,
                2,
                "块"),

            Item(
                "gpam_battery_charger",
                "电源与充电",
                "电池充电器",
                "锂电池充电器",
                "1套",
                Optional,
                "检查充电器外观、插头和输出标识；现有实物标签为 DC 12.6 V 时核对与两块电池匹配，并逐块验证充电。",
                "用户实物",
                true,
                1,
                1,
                "套"),

            Item(
                "gpam_probe_signal_cable",
                "线缆与转接",
                "探头延长线",
                "探头信号连接线",
                "1根",
                Required,
                "蓝色编织线；检查两端航空插头、锁紧环、线缆屏蔽层及导通。",
                "用户实物",
                true,
                1,
                1,
                "根"),

            Item(
                "gpam_power_cable",
                "线缆与转接",
                "电源线/航空插头线",
                "设备供电连接线",
                "1根",
                Required,
                "黑色长线；检查两端插头、极性、导通及绝缘。",
                "用户实物",
                true,
                1,
                1,
                "根"),

            Item(
                "gpam_dc_adapter",
                "线缆与转接",
                "DC转接线",
                "DC供电转接短线",
                "1根",
                Optional,
                "袋装短线；核对航空接口与 DC 圆孔接口规格，检查极性。",
                "用户实物",
                true,
                1,
                1,
                "根"),

            Item(
                "gpam_usb_cable",
                "线缆与转接",
                "USB线",
                "USB数据/充电线",
                "1根",
                Optional,
                "检查插头类型、数据通信和充电功能，不只测试“能充电”。",
                "用户实物",
                true,
                1,
                1,
                "根"),

            Item(
                "gpam_mount_clamps",
                "安装附件",
                "安装支架/机臂夹具",
                "无人机载荷安装夹具",
                "4个",
                Optional,
                "确认四个均齐全；逐个检查翼形螺母、长螺钉、固定螺母、橡胶垫及夹口。",
                "用户实物",
                true,
                4,
                4,
                "个"),

            Item(
                "gpam_green_clips",
                "安装附件",
                "塑料卡扣",
                "管夹/限位卡扣",
                "4个",
                Required,
                "4 个齐全；检查开裂、变形和卡紧状态；建议首次安装确认各卡扣对应位置并在箱内/照片中留存定位。",
                "用户实物",
                true,
                4,
                4,
                "个"),

            Item(
                "gpam_strap",
                "安装附件",
                "尼龙绑带",
                "背带/固定带",
                "1条",
                Optional,
                "检查织带、调节扣、末端连接环是否完整。",
                "用户实物",
                true,
                1,
                1,
                "条"),

            Item(
                "gpam_spare_fasteners",
                "工具与备件",
                "手拧螺钉/小螺丝",
                "备用紧固件",
                "1袋",
                Optional,
                "拆袋逐件计数并记录规格。",
                "用户实物",
                true,
                1,
                1,
                "袋"),

            Item(
                "gpam_hex_tool",
                "工具与备件",
                "内六角螺丝刀",
                "2.5 mm 六角工具",
                "1把",
                Optional,
                "检查刀头磨损；确认是否为安装夹具/探杆专用。",
                "用户实物",
                true,
                1,
                1,
                "把"),

            Item(
                "gpam_case_keys",
                "工具与备件",
                "钥匙",
                "设备箱钥匙",
                "2把",
                Optional,
                "两把分别试开箱锁；建议编号并留一把备用。",
                "用户实物",
                true,
                2,
                2,
                "把"),

            Item(
                "gpam_transport_case",
                "包装与运输",
                "铝合金仪器箱",
                "航磁设备运输箱",
                "1只",
                Required,
                "检查箱锁、提手、合页、包角及内部槽位，确认所有槽位都有对应物品。",
                "用户实物",
                true,
                1,
                1,
                "只")
        ]);

    private static readonly EquipmentChecklistTemplate DjiMatrice350Rtk = new(
        "dji-matrice-350-rtk",
        "DJI Matrice 350 RTK 出发设备清点",
        [
            "DJI Matrice 350 RTK",
            "Matrice 350 RTK",
            "M350 RTK",
            "M350",
            "DJ350",
            "经纬 M350 RTK"
        ],
        "v0.6.4 现场清点修正版：15 项，其中 9 项必备、6 项可选。"
        + "物品构成依据 DJI Matrice 350 RTK 官方 In the Box，检查内容结合 Safety Guidelines。"
        + "必备/可选为当前项目管理规则，不是 DJI 官方对装箱物品的等级定义。",
        [
            Item(
                "m350_aircraft",
                "飞行器本体",
                "飞行器",
                "DJI Matrice 350 RTK 飞行器",
                "1 台",
                Required,
                "检查机体、机臂、接口和结构无损伤；机内无水/油/泥沙等异物，通风/电机散热孔无遮挡；"
                + "机臂展开并锁紧；电机清洁、转动顺畅无异常声；视觉/FPV/红外/辅助灯等传感器清洁。",
                "DJI In the Box + Safety Guidelines"),

            Item(
                "m350_landing_gear",
                "飞行器本体",
                "起落架",
                "起落架（Landing Gear）",
                "2 个",
                Required,
                "2 个齐全；检查套筒、紧固处和杆体无裂纹/变形/松动；起飞前确认起落架套筒牢固。",
                "DJI In the Box + Safety Guidelines"),

            Item(
                "m350_2110s_propeller",
                "飞行器本体",
                "螺旋桨",
                "2110s 螺旋桨（随箱备用对）",
                "1 对",
                Required,
                "清点随箱 1 对备用桨；飞行前检查机上全部螺旋桨及适配器无老化、缺口、裂纹、变形或松动，"
                + "安装牢固，并确认桨叶/机臂已展开。",
                "DJI In the Box + Safety Guidelines"),

            Item(
                "m350_tb65",
                "电池与充电",
                "飞行电池",
                "TB65 智能飞行电池",
                "2 块",
                Required,
                "2 块成对检查电量、外壳、触点和锁止状态，无鼓包/渗漏/破损；装机后确认释放拨杆锁定。"
                + "同时核对电池配组贴纸/编号规则清楚，两块 TB65 保持同组管理并共同充放电；贴纸不再单独设清点条目。",
                "DJI In the Box + Safety Guidelines",
                true,
                2,
                2,
                "块"),

            Item(
                "m350_rc_plus",
                "遥控与通信",
                "遥控器",
                "DJI RC Plus 遥控器",
                "1 台",
                Required,
                "电量充足；屏幕、摇杆、按键、接口及天线正常；与飞行器完成链路并确认遥控权，"
                + "飞行模式/状态检查正常；DJI Pilot 2 与固件处于已核准/最新状态。",
                "DJI In the Box + Safety Guidelines"),

            Item(
                "m350_wb37",
                "遥控与通信",
                "遥控器电池",
                "WB37 智能电池",
                "1 块",
                Required,
                "检查电量、外观和触点；装入遥控器后锁止并可稳定供电；充电后按解锁机构规范取出。",
                "DJI In the Box + Safety Guidelines",
                true,
                1,
                1,
                "块"),

            Item(
                "m350_spare_gimbal_damper",
                "备件与维护",
                "减震球",
                "备用减震球（Spare Gimbal Damper）",
                "4 个",
                Optional,
                "4 个齐全，无老化、裂纹、撕裂或变形；作为更换件保持清洁并防止挤压。",
                "DJI In the Box"),

            Item(
                "m350_screws_tools",
                "备件与维护",
                "维修工具",
                "维修工具套装（Screws and Tools）",
                "1 套",
                Optional,
                "按官方随箱套装核对工具及多组螺钉；工具无明显磨损，螺钉无滑牙/锈蚀/变形，使用后归位。",
                "DJI In the Box"),

            Item(
                "m350_cleaning_tools",
                "备件与维护",
                "清洁工具",
                "清洁工具套装（Cleaning Tools）",
                "1 套",
                Optional,
                "确认清洁工具齐全且洁净；用于视觉/FPV/红外等传感器和接口时避免刮伤、污染光学表面。",
                "DJI In the Box + Safety Guidelines"),

            Item(
                "m350_rubber_port_cover",
                "备件与维护",
                "接口保护",
                "胶塞（套）（Rubber Port Cover Set）",
                "1 套",
                Optional,
                "检查各接口胶塞/保护盖无开裂、变形或遗失；未使用的 E-Port、PSDK/加密狗仓等接口按要求保持关闭。",
                "DJI In the Box + Safety Guidelines"),

            Item(
                "m350_manuals",
                "资料",
                "文档",
                "文档（Manuals）",
                "1 套",
                Optional,
                "确认使用说明、安全概要等电子/纸质资料可查；版本与当前设备/固件尽量一致。",
                "DJI In the Box"),

            Item(
                "m350_carrying_case",
                "包装运输",
                "运输箱",
                "运输箱（Carrying Case）",
                "1 只",
                Required,
                "检查箱体、锁扣、提手/轮组、内衬和槽位完好；按箱内布局固定机体、TB65、WB37 和载荷附件，"
                + "避免运输中松动。",
                "DJI In the Box"),

            Item(
                "m350_bs65",
                "电池与充电",
                "智能电池箱",
                "BS65 智能电池箱",
                "1 台",
                Required,
                "检查箱体、接口和供电线缆；上电后确认 TB65/WB37 充电位工作正常。"
                + "官方物品清单标注 BS65 独立购买、分开发货。",
                "DJI In the Box"),

            Item(
                "m350_usb_c",
                "电池与充电",
                "USB-C 数据线",
                "双头 USB-C 数据线",
                "1 根",
                Optional,
                "检查线缆和两端 USB-C 接口无破损/松动，实际测试数据通信与充电功能。",
                "DJI In the Box"),

            Item(
                "m350_power_cable",
                "电池与充电",
                "电源线",
                "电源线（Power Cable）",
                "按销售地区实物",
                Required,
                "核对插头制式、线缆绝缘和额定标识；与当地电源及 BS65 匹配。"
                + "DJI 官方明确电源线类型和数量因销售地区而异，以实物为准。",
                "DJI In the Box")
        ]);

    public static IReadOnlyList<EquipmentChecklistTemplate> All =>
        [Dji, Gsm, GpamM350Rm, DjiMatrice350Rtk];

    public static EquipmentChecklistTemplate? Get(
        string? templateId) =>
        All.FirstOrDefault(x =>
            string.Equals(
                x.TemplateId,
                templateId?.Trim(),
                StringComparison.OrdinalIgnoreCase));

    public static EquipmentChecklistTemplate? Find(
        params string?[] values)
    {
        var candidates = values
            .Where(x =>
                !string.IsNullOrWhiteSpace(x))
            .Select(Normalize)
            .ToHashSet(
                StringComparer.OrdinalIgnoreCase);

        if (candidates.Count == 0)
            return null;

        foreach (EquipmentChecklistTemplate template in All)
        {
            var aliases = template.Aliases
                .Select(Normalize)
                .ToHashSet(
                    StringComparer.OrdinalIgnoreCase);

            if (candidates.Overlaps(aliases))
                return template;
        }

        return null;
    }

    private static string Normalize(string? value) =>
        string.Concat(
            (value ?? "")
                .ToLowerInvariant()
                .Replace('－', '-')
                .Replace('—', '-')
                .Where(c =>
                    !char.IsWhiteSpace(c)));
}
