# 配置说明（CONFIG）

公开版本里所有「我们自己的服务」都被换成了占位符。按本文填完，即可自己跑起来。

不填也能用一部分功能：**同一台电脑 + 平板、走局域网直连**的场景下，只要两端网络参数一致，就能连；但跨网络（不在同一个 WiFi）必须有一个会合节点，所以建议按下面配好。

---

## 一、先选：会合节点用哪个

会合节点只负责「帮两台设备互相找到」，**不转发画面**。两种选择：

| 选择 | 适合谁 | 代价 |
|---|---|---|
| **A. 用公共节点**（EasyTier 社区公开的） | 想马上试、不想自己运维 | 依赖第三方，稳定性不由你控制 |
| **B. 自建节点**（一台有公网 IP 的小服务器） | 长期使用、要求稳定 | 需要一台服务器（1 核 1G 足够） |

公共节点示例（写进代码即可）：

```
tcp://et1.fuis.top:11010
```

> 公共节点地址会变，请以 EasyTier 官方文档/社区公布为准。本项目与这些节点没有隶属关系。

---

## 二、电脑端要改的地方（共 3 处）

### 1. 会合节点地址

文件：`tray2/NetworkProvision.cs`

```csharp
// 自建会合节点：只帮牵线，不转发数据
const string HubPeers =
    "-p tcp://hub.example.com:11010 -p wss://hub.example.com:11012";
```

改成你自己的节点地址；只想用公共节点就写：

```csharp
const string HubPeers = "-p tcp://et1.fuis.top:11010";
```

下面这行是「自建节点不通时才会用到」的公共节点兜底，可保留也可按需改：

```csharp
const string PublicPeer = "-p tcp://et1.fuis.top:11010";
```

### 2. 安装时写入的节点地址

文件：`installer/ps/install-post.ps1`，找到 `$etArgs`：

```powershell
$etArgs = "--network-name $netName --network-secret $netSecret -i $hostIp -p tcp://hub.example.com:11010 -p wss://hub.example.com:11012"
```

把它换成和上面一致的地址。**注意**：这里不要再写死网络名和密码，脚本会自动生成/读取 `C:\ProgramData\LingWangRemote\network.json`。

### 3. 云账号（可选）

文件：`tray/AccountClient.cs`

```csharp
public const string EnvId = "your-env-id";
public const string ApiBase = "https://your-env-id.api.tcloudbasegateway.com";
```

- 不打算做账号体系：保持占位符即可，程序会退化成「只用 6 位控制码」模式；
- 想用账号：换成你自己的腾讯云 CloudBase 环境 ID 与网关地址，并在云端开启手机号登录。

---

## 三、平板端要改的地方（共 3 处）

### 1. 会合节点

文件：`entry/src/main/ets/service/networking/EasyTierManager.ets`

```ts
private static readonly HUB_PEER_TCP: string = 'tcp://hub.example.com:11010';
private static readonly HUB_PEER_WSS: string = 'wss://hub.example.com:11012';
```

### 2. 节点探测地址

文件：`entry/src/main/ets/service/networking/VpnController.ets`

```ts
const HUB_HOST = 'hub.example.com';
const HUB_PORT = 11010;
```

> 探测只用来判断「自建节点是否连得上」，连不上时会弹窗让用户选择是否临时改用公共节点。不弹窗就把 `MAX_ATTEMPTS` 附近的逻辑按注释调整。

### 3. 云账号（可选）

文件：`entry/src/main/ets/service/account/AccountCenterService.ets`

```ts
const ENV_ID: string = 'your-env-id';
const API_BASE: string = 'https://your-env-id.api.tcloudbasegateway.com';
const REST_BASE: string = 'https://your-env-id.api.tcloudbasegateway.com/v1/rdb/rest';
```

留占位符时，平板端「账号」页面会提示不可用，其它功能不受影响。

---

## 四、网络名与密码（不用手动填）

默认行为已经替你处理好：

- 电脑端首次安装时自动生成一组随机网络名与密码，存在
  `C:\ProgramData\LingWangRemote\network.json`；
- 平板端配对时（同账号自动配对，或输入 6 位控制码）由电脑端下发，平板保存后使用。

所以**不需要**把密码写进任何配置文件。想手动固定一套，也可以直接改这个 json：

```json
{ "name": "open-remote-2026", "secret": "你自己的一串随机字符" }
```

改完重启 `LingWangStreamService` 服务与 EasyTier 服务生效；平板端需要重新配对一次。

---

## 五、自建会合节点最小做法（可跳过）

在一台有公网 IP 的 Linux 上装 EasyTier，用「共享节点、只牵线」模式：

```bash
# 1) 安装 EasyTier（以官方发布包为准）
# 2) 启动为共享节点：不开虚拟网卡、不加入任何网络、只帮别人建连接
easytier-core --no-tun --relay-all-peer-rpc \
  --relay-network-whitelist ""
# 3) 放行端口：TCP/UDP 11010（WSS 另行配置 11012）
```

要点：

- **不要**给它配置 `--network-name` / `--network-secret`，它不加入任何人的网络；
- 白名单留空 + 允许转发 RPC，表示「只帮建 P2P，不转发业务数据」；
- 服务器安全组放行 TCP 11010、UDP 11010（以及 WSS 用的 11012）。

---

## 六、没有账号怎么用（控制码配对）

1. 电脑端打开程序，点「生成控制码」，得到 6 位数字（默认 10 分钟内有效）；
2. 平板端点那台电脑的卡片，选择「输入控制码」，填这 6 位；
3. 校验通过后自动配对并连接；之后这台电脑会留在列表里。

---

## 七、验证清单

- [ ] 电脑端服务在跑：任务管理器里能看到 `LingWangStreamSvc`
- [ ] 电脑端界面显示「控制校验口已启动」
- [ ] 两端会合节点地址、端口完全一致
- [ ] 自建节点：服务器安全组已放行 TCP/UDP 11010
- [ ] 平板端能连上电脑并看到桌面
- [ ] 需要账号功能时：`EnvId` / `ApiBase` 已换成自己的，且云端开启了手机号登录

---

## 八、常见问题

**连不上，提示超时**
先确认两端会合节点一致；再看电脑端是否被防火墙拦了（首次运行会弹授权，需允许）。

**一直显示「正在建立安全连接」**
自建节点可能不通，切换成公共节点试一次；或检查服务器端口是否放行。

**账号页面提示不可用**
`EnvId` 还是占位符。要么换成自己的云环境，要么忽略——控制码配对不受影响。

**平板和电脑不在同一个 WiFi**
必须有可用的会合节点（公共或自建），否则互相找不到。
