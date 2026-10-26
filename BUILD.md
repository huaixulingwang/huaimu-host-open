# 构建说明（BUILD）

## 环境

| 用途 | 需要什么 |
|---|---|
| 电脑端代码 | .NET Framework 4.8 SDK（`dotnet build` 即可） |
| 安装包 | Inno Setup 7（或 6）、PowerShell 5.1 |
| 平板端 | DevEco Studio + HarmonyOS SDK（API 20 及以上）、Node.js、hvigor |

## 电脑端

```powershell
# 1) 串流服务
powershell -File streamsvc\build-streamsvc.ps1

# 2) 托盘（界面在 tray2\ui，业务在 tray2\*.cs）
powershell -File tray\build-tray.ps1

# 3) 拼装货舱（把 Sunshine / EasyTier / NSSM / 驱动脚本汇成 payload）
powershell -File build\assemble-payload.ps1

# 4) 出安装包（成品在 dist\）
powershell -File build\make-installer.ps1
```

### 需要自己准备的东西（不在仓库里）

1. **第三方二进制**：Sunshine、EasyTier、NSSM 请从各自官方发布页下载，放到 `payload/` 对应子目录（参考 `payload/MANIFEST.txt` 的清单与校验值）。
2. **虚拟显示器 / 虚拟鼠标驱动**：仓库不包含，请从你信任的 Sunshine 生态项目获取；没有它时隐私屏与虚拟屏功能不可用，其它功能正常。
3. **证书**：安装包会导入自签根证书与驱动签名证书，请自行生成（`payload/cert`）。**私钥绝不能提交。**
4. **组网身份**：安装脚本会在安装时生成或读取 `C:\ProgramData\LingWangRemote\network.json`；不提供时安装包会自动为每台电脑随机生成一套。

> 本仓库**不包含**任何真实服务器地址、组网密码或云服务密钥。你需要自备 EasyTier 会合节点（公共节点或自建），在 `tray2/NetworkProvision.cs` 与 `installer/ps/install-post.ps1` 中按注释替换成自己的地址。

## 平板端

```powershell
# 在工程根目录
$env:JAVA_HOME='<你的 JDK 目录>'
node hvigorw.js assembleHap --mode module -p product=default --no-daemon
```

### 需要自己准备的东西

1. **签名**：`build-profile.json5` 里的签名配置需绑定你自己的证书（DevEco Studio 里生成调试授权，或使用正式证书）。
2. **组网密钥**：`entry/src/main/ets/common/Secrets.ets` 按 `.example` 模板创建（不提交）。
3. **子模块**：`git submodule update --init --recursive`。

## 常见问题

- 用中文路径构建容易出编码问题，建议工程放在纯英文路径下。
- 平板端联网调试：`hdc tconn <平板IP>:<无线调试端口>`，端口每次开关都会变。
- 电脑端服务装在 `C:\Program Files\LingWangRemote`，数据与日志在 `C:\ProgramData\LingWangRemote`。
