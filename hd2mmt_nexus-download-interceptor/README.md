# Nexus Download Interceptor

一个用于拦截 Nexus Mods 下载链接并发送到 Helldivers 2 Mod Manager 的浏览器扩展。

## 功能特性

- 自动拦截 Nexus Mods CDN 的下载请求
- 仅拦截 Helldivers 2 (游戏ID: 6119) 的模组下载
- 将下载请求转发到本地 Mod Manager 进行管理
- 支持启用/禁用拦截功能
- 显示管理器连接状态
- 下载进度通知

## 系统要求

- Firefox 109.0 或更高版本
- Helldivers 2 Mod Manager (需在本地运行)

## 安装说明

### 开发模式安装

1. 打开 Firefox 浏览器
2. 输入 `about:debugging#/runtime/this-firefox` 进入调试页面
3. 点击 "临时加载附加组件"
4. 选择扩展目录中的 `manifest.json` 文件

### 权限说明

本扩展需要以下权限：

- `downloads`: 访问下载管理功能
- `webRequest`: 拦截网络请求
- `webRequestBlocking`: 阻止特定请求
- `storage`: 保存用户设置
- `notifications`: 显示通知消息

## 使用说明

1. 确保 Helldivers 2 Mod Manager 已启动
2. 点击浏览器工具栏中的扩展图标
3. 启用 "启用拦截" 开关
4. 在 Nexus Mods 网站下载 Helldivers 2 模组时，扩展会自动拦截并转发到管理器

## 技术细节

### 拦截规则

扩展仅拦截以下条件的下载请求：
- URL 包含 `files.nexus-cdn.com`
- 路径中的游戏 ID 为 `6119` (Helldivers 2)

### 本地服务器通信

扩展通过以下端点与 Mod Manager 通信：
- `http://localhost:12345/7456/download` - 发送下载请求
- `http://localhost:12345/7456/download/health` - 检查管理器状态

## 隐私政策

本扩展：
- 不会收集或传输用户的个人数据
- 仅在本地存储用户设置（启用/禁用状态）
- 所有网络请求仅在本地进行，不会发送到第三方服务器

## 开源许可证

MIT License - 详见 LICENSE 文件

## 项目地址

https://github.com/TYHH100/Helldivers2ModManager/tree/test