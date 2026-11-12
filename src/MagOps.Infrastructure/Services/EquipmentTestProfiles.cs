using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed record EquipmentTestDefinition(
    string TestType,
    string Check);

public static class EquipmentTestProfiles
{
    private static readonly IReadOnlyList<EquipmentTestDefinition> Aircraft =
    [
        new("开机与飞控自检", "飞行器、遥控器正常上电；DJI Pilot 2/飞控无阻断性告警，电池、电机、传感器状态可用于飞行。"),
        new("遥控与图传链路", "确认遥控权、控制链路和图传稳定；天线状态正常，无异常掉线或明显延迟。"),
        new("GNSS/RTK 定位", "确认卫星定位可用；任务需要 RTK 时检查 RTK 状态、定位精度与航向状态满足起飞条件。"),
        new("IMU/指南针/姿态", "确认 IMU、指南针、姿态与航向显示正常，无需校准或存在磁干扰等阻断性告警。"),
        new("电机与动力系统", "检查电机启动/停机、转速和声音无异常；桨叶方向、锁止及动力系统状态满足飞行要求。"),
        new("悬停与基础操控", "在安全条件下进行短时悬停/基础操控，确认高度、位置、偏航及杆量响应稳定。"),
        new("返航与失联保护", "确认返航点、返航高度及失联动作设置正确；按任务条件核查返航逻辑可用。"),
        new("航线任务执行", "加载实际或测试航线，核查航点/航向/速度/高度等飞行任务参数可被飞控正确读取并执行。"),
        new("避障与感知系统", "检查视觉、红外、LiDAR/避障等已安装感知系统状态；按任务要求确认避障设置和提示正常。")
    ];

    private static readonly IReadOnlyList<EquipmentTestDefinition> Magnetometer =
    [
        new("上电与供电稳定性", "设备正常上电，供电电压/电池状态稳定，无异常重启或掉电。"),
        new("探头连接与磁场读数", "探头、连接线及采集器通信正常，磁场读数连续且无明显异常跳变。"),
        new("定位与时间同步", "GPS/北斗定位与时间同步正常，记录时间基准满足任务要求。"),
        new("无线通信", "如任务使用无线终端，确认配对、通信和状态回传稳定。"),
        new("数据记录", "启动实际采集并确认数据持续写入，记录字段、采样率和时间连续性符合任务要求。"),
        new("数据导出", "完成一次实际数据读取/导出，确认文件可打开、字段完整且时间范围正确。"),
        new("静态一致性", "静置条件下检查短时读数稳定性、重复性及明显异常值。"),
        new("现场磁环境检查", "检查设备安装位置和周边磁性物体影响，确认现场磁环境满足航磁采集要求。")
    ];

    private static readonly IReadOnlyList<EquipmentTestDefinition> BaseStation =
    [
        new("开机与供电", "主机正常开机，供电/电池状态稳定，无异常重启或低电压阻断。"),
        new("探头与主机连接", "探头连接正常，主机可持续读取有效磁场数据。"),
        new("定位与时间同步", "GPS/时间同步状态满足日变记录需要，时间基准与项目设置一致。"),
        new("数据记录", "启动实际记录并确认采样持续、时间连续、字段完整。"),
        new("存储状态", "检查剩余存储空间和写入状态，确保可覆盖计划观测时长。"),
        new("数据导出", "完成一次数据读取/导出，确认文件可正常打开并包含预期时间段。"),
        new("静态一致性", "静置观测短时数据，检查读数连续性、稳定性及明显异常点。"),
        new("站点磁环境", "确认日变站周围无显著移动磁性干扰源，站点条件适合连续观测。")
    ];

    private static readonly IReadOnlyList<EquipmentTestDefinition> General =
    [
        new("开机检查", "确认设备可正常上电、自检并进入可工作状态。"),
        new("连接检查", "确认任务所需线缆、接口或无线连接稳定。"),
        new("功能检查", "按该设备在当前项目中的实际用途执行一次主要功能测试。"),
        new("现场环境检查", "确认现场环境不会对设备的主要功能造成明显干扰。")
    ];

    public static IReadOnlyList<EquipmentTestDefinition> For(EquipmentRecord equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        string category = EquipmentCatalogService.NormalizeCategory(equipment.Category);
        string model = EquipmentCatalogService.NormalizeModel(equipment.Model);

        if (category.Equals("无人机", StringComparison.OrdinalIgnoreCase) ||
            model.Equals(EquipmentCatalogService.DjiMatrice400, StringComparison.OrdinalIgnoreCase) ||
            model.Equals(EquipmentCatalogService.DjiMatrice350Rtk, StringComparison.OrdinalIgnoreCase))
        {
            return Aircraft;
        }

        if (category.Equals(EquipmentCatalogService.MagnetometerSensorCategory, StringComparison.OrdinalIgnoreCase) ||
            model.Equals(EquipmentCatalogService.GpamM350Rm, StringComparison.OrdinalIgnoreCase))
        {
            return Magnetometer;
        }

        if (category.Equals("日变基站", StringComparison.OrdinalIgnoreCase) ||
            model.Equals("GSM-19T", StringComparison.OrdinalIgnoreCase))
        {
            return BaseStation;
        }

        return General;
    }
}
