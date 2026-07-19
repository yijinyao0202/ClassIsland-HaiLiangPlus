# 大周值日生（已并入 HL Education +）

从 HL Education + `2.0.0.0` 起，值日生服务、设置页、主界面组件和提醒均直接编入 `ClassIsland.HaiGao104.dll`，不再单独生成或安装 `ClassIsland.HaiGaoDuty.cipx`。

本项目目录继续保留，作为 HL Education + 共享源码和 `ClassIsland.HaiGaoDuty.Tests` 的测试载体。用户只需安装 `ClassIsland.HaiGao104.cipx`；旧独立插件应移出 ClassIsland 插件加载目录，旧配置会在主配置不存在时自动复制且不会被删除。
