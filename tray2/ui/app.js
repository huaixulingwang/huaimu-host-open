/* 电脑端界面逻辑：只负责展示与交互，业务在 C# 侧 */

const $ = (id) => document.getElementById(id);

const state = {
  loggedIn: false,
  code: '',
  mode: 'sms',
  verificationId: '',
  isUser: true,
  countdown: 0
};

let bridgeSeq = 0;
const pending = new Map();

/* ── 与 C# 桥 ─────────────────────────── */

function post(action, payload) {
  return new Promise((resolve) => {
    const id = String(++bridgeSeq);
    pending.set(id, resolve);
    const message = Object.assign({ action, id }, payload || {});
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(message);
    } else {
      pending.delete(id);
      resolve({ error: '界面桥未就绪' });
    }
  });
}

window.onBridgeResult = (result) => {
  const resolve = pending.get(result.id);
  if (!resolve) return;
  pending.delete(result.id);
  // C# 侧把 data 作为 JSON 字符串传过来，这里统一还原成对象
  let data = result ? result.data : null;
  if (typeof data === 'string') {
    try { data = JSON.parse(data); } catch (_) { data = {}; }
  }
  resolve(data || {});
};

/* ── 状态渲染 ─────────────────────────── */

window.renderState = (s) => {
  const wasLoggedIn = state.loggedIn;
  state.loggedIn = !!s.loggedIn;
  state.deviceId = s.deviceId || '';

  $('machineName').textContent = s.machine || '这台电脑';
  $('deviceId').textContent = s.deviceId || '—';
  $('virtualIp').textContent = s.virtualIp || '—';

  const online = !!s.sunshine && !!s.easytier;
  const onlinePill = $('onlinePill');
  if (online) {
    onlinePill.textContent = '在线';
    onlinePill.className = 'pill ok';
  } else {
    onlinePill.textContent = '服务异常';
    onlinePill.className = 'pill bad';
  }

  setPill('pillSunshine', s.sunshine, '正常', '异常');
  setPill('pillEasytier', s.easytier, '正常', '异常');
  setPill('pillGate', s.gateRunning, '已启动', '未启动');

  $('gateState').className = 'gate-state' + (s.gateRunning ? ' on' : '');
  $('gateText').textContent = s.gateRunning ? '控制校验口已启动' : '校验口未启动';

  $('connTitle').textContent = s.sessionActive ? '有设备正在连接' : '没有设备连接';
  $('connDetail').textContent = s.sessionDetail || '平板连上来后，这里会显示画面规格';
  $('connEmpty').className = 'conn-empty' + (s.sessionActive ? ' active' : '');

  renderCode(s);

  const heroAccount = $('heroAccount');
  heroAccount.textContent = s.loggedIn ? (s.accountName || '已登录') : '未登录';
  heroAccount.style.color = s.loggedIn ? 'var(--primary-dark)' : 'var(--text-3)';

  if (s.loggedIn) {
    $('accountLoggedOut').hidden = true;
    $('accountLoggedIn').hidden = false;
    $('accountName').textContent = s.accountName || '已登录';
    $('avatar').textContent = (s.accountName || 'L').slice(0, 1).toUpperCase();
    $('accountSub').textContent = s.netScoped
      ? '云端功能已开启 · 已加入这个账号的专属网络'
      : '云端功能已开启 · 专属网络还没切换，下次启动会再提示授权';
  } else {
    $('accountLoggedOut').hidden = false;
    $('accountLoggedIn').hidden = true;
  }

  // 退出账号立刻清掉设备名单；登录成功后从云端同步一次
  const deviceList = $('deviceList');
  if (deviceList) {
    if (!state.loggedIn) {
      deviceList.innerHTML = '<li class="device-empty">登录后可查看</li>';
    } else if (!wasLoggedIn) {
      loadDevices();
    }
  }
};

function setPill(id, ok, okText, badText) {
  const el = $(id);
  el.textContent = ok ? okText : badText;
  el.className = 'pill ' + (ok ? 'ok' : 'bad');
}

function renderCode(s) {
  const number = $('codeNumber');
  const hint = $('codeHint');
  const copy = $('btnCopyCode');

  if (s.code) {
    number.textContent = s.code.slice(0, 3) + ' ' + s.code.slice(3);
    number.classList.remove('empty');
    hint.textContent = '约剩 ' + s.codeMinutes + ' 分钟；到期自动失效';
    copy.disabled = false;
    state.code = s.code;
  } else {
    number.textContent = '点击生成';
    number.classList.add('empty');
    hint.textContent = '点「生成」创建一串 6 位数字，10 分钟内有效';
    copy.disabled = true;
    state.code = '';
  }
}

/* ── 设备名单 ─────────────────────────── */

let devicesLoading = false;

async function loadDevices() {
  const list = $('deviceList');
  if (!list) return;
  if (!state.loggedIn) {
    list.innerHTML = '<li class="device-empty">登录后可查看</li>';
    return;
  }
  if (devicesLoading) return;
  devicesLoading = true;
  list.innerHTML = '<li class="device-empty">正在加载…</li>';

  let rows = [];
  try {
    const r = await post('listDevices');
    rows = JSON.parse((r && r.devices) || '[]');
  } catch (_) {
    rows = [];
  }
  devicesLoading = false;

  list.innerHTML = '';
  if (!rows.length) {
    list.innerHTML = '<li class="device-empty">这个账号还没有设备记录</li>';
    return;
  }
  rows.forEach((d) => {
    const li = document.createElement('li');
    li.className = 'device-row';

    const info = document.createElement('div');
    info.className = 'device-info';
    const name = document.createElement('div');
    name.className = 'device-name';
    name.textContent = d.device_name || '未命名设备';
    if (state.deviceId && d.device_id === state.deviceId) {
      const tag = document.createElement('span');
      tag.className = 'tag';
      tag.textContent = '本机';
      name.appendChild(tag);
    }
    const meta = document.createElement('div');
    meta.className = 'device-meta';
    const type = d.device_type === 'pc' ? '电脑' : (d.device_type === 'phone' ? '手机' : '平板');
    const seen = d.last_seen_at ? new Date(d.last_seen_at).toLocaleString('zh-CN', { hour12: false }) : '';
    meta.textContent = type + (seen ? ' · 最近登录 ' + seen : '');
    info.appendChild(name);
    info.appendChild(meta);

    const btn = document.createElement('button');
    btn.className = 'btn ghost small danger';
    btn.textContent = '删除';
    btn.addEventListener('click', async () => {
      if (!window.confirm('从账号里移除「' + (d.device_name || '这台设备') + '」？')) return;
      btn.disabled = true;
      btn.textContent = '删除中…';
      await post('deleteDevice', { deviceId: d.device_id });
      loadDevices();
    });

    li.appendChild(info);
    li.appendChild(btn);
    list.appendChild(li);
  });
}

/* ── 页面切换 ─────────────────────────── */

function gotoPage(page) {
  document.querySelectorAll('.nav-item').forEach((item) => {
    item.classList.toggle('active', item.dataset.page === page);
  });
  $('pageOverview').hidden = page !== 'overview';
  $('pageDevices').hidden = page !== 'devices';
  $('pageAccount').hidden = page !== 'account';
  $('pageSettings').hidden = page !== 'settings';
  if (page === 'devices') loadDevices();
  if (page === 'settings') loadSettings();
}

/* ── 设置：开机自启 / 关闭按钮行为 ─────────── */

async function loadSettings() {
  const result = await post('getSettings');
  const value = (result && result.closeAction) || 'ask';
  document.querySelectorAll('input[name="closeAction"]').forEach((el) => {
    el.checked = el.value === value;
  });
  const auto = $('autoStartToggle');
  if (auto) auto.checked = !!(result && result.autoStart);
}

document.querySelectorAll('input[name="closeAction"]').forEach((el) => {
  el.addEventListener('change', () => {
    if (el.checked) post('setSettings', { closeAction: el.value });
  });
});

const autoStartToggle = $('autoStartToggle');
if (autoStartToggle) {
  autoStartToggle.addEventListener('change', () => {
    post('setSettings', { autoStart: autoStartToggle.checked ? '1' : '0' });
  });
}

/* ── 关闭按钮：首次询问 ───────────────── */

window.showCloseDialog = () => {
  $('closeMask').hidden = false;
};

$('btnCloseCancel').addEventListener('click', () => {
  $('closeMask').hidden = true;
});

$('btnCloseOk').addEventListener('click', () => {
  const chosen = document.querySelector('input[name="closeChoice"]:checked');
  const action = chosen ? chosen.value : 'hide';
  const remember = $('closeRemember').checked ? '1' : '0';
  $('closeMask').hidden = true;
  post('closeResolve', { action: action, remember: remember });
});


window.gotoPage = gotoPage;

document.querySelectorAll('.nav-item').forEach((item) => {
  item.addEventListener('click', () => gotoPage(item.dataset.page));
});

/* ── 窗口按钮 ─────────────────────────── */

$('btnMin').addEventListener('click', () => post('minimize'));
$('btnClose').addEventListener('click', () => post('close'));

let dragging = false;

function startDrag(e) {
  if (e.button !== 0) return;
  // 标题栏上的窗口按钮不能被拖拽逻辑吞掉点击
  if (e.target.closest('.win-btn')) return;
  dragging = true;
  post('drag');
  setTimeout(() => { dragging = false; }, 120);
}

$('titlebar').addEventListener('mousedown', startDrag);
document.querySelector('.brand').addEventListener('mousedown', startDrag);

/* ── 概览操作 ─────────────────────────── */

$('btnRefresh').addEventListener('click', () => post('ready'));

$('heroAccount').addEventListener('click', () => gotoPage('account'));

$('btnGenCode').addEventListener('click', async () => {
  await post('genCode');
  post('ready');
});

$('btnCopyCode').addEventListener('click', () => {
  if (state.code) post('copy', { text: state.code });
});

$('btnOpenLogs').addEventListener('click', () => post('openLogs'));

$('btnLogout').addEventListener('click', () => post('logout'));
$('btnOpenAccount').addEventListener('click', () => gotoPage('account'));
$('btnRefreshDevices').addEventListener('click', () => loadDevices());

/* ── 账号：方式切换 ───────────────────── */

document.querySelectorAll('.seg').forEach((seg) => {
  seg.addEventListener('click', () => {
    document.querySelectorAll('.seg').forEach((s) => s.classList.remove('active'));
    seg.classList.add('active');
    state.mode = seg.dataset.mode;
    $('formSms').hidden = state.mode !== 'sms';
    $('formPassword').hidden = state.mode !== 'password';
    $('smsError').textContent = '';
    $('passwordError').textContent = '';
  });
});

/* ── 手机号输入清洗 ───────────────────── */

$('phoneSms').addEventListener('input', (e) => {
  const cleaned = e.target.value.replace(/\D/g, '').slice(0, 11);
  if (e.target.value !== cleaned) e.target.value = cleaned;
  $('btnSendCode').disabled = cleaned.length !== 11 || state.countdown > 0;
  $('smsError').textContent = '';
});

$('smsCode').addEventListener('input', (e) => {
  const cleaned = e.target.value.replace(/\D/g, '').slice(0, 6);
  if (e.target.value !== cleaned) e.target.value = cleaned;
  $('smsError').textContent = '';
});

$('account').addEventListener('input', () => { $('passwordError').textContent = ''; });
$('password').addEventListener('input', () => { $('passwordError').textContent = ''; });

$('btnEye').addEventListener('click', () => {
  const input = $('password');
  const show = input.type === 'password';
  input.type = show ? 'text' : 'password';
  $('btnEye').textContent = show ? '隐藏' : '显示';
});

/* ── 发送验证码 ───────────────────────── */

$('btnSendCode').addEventListener('click', async () => {
  const phone = $('phoneSms').value.trim();
  if (phone.length !== 11) {
    $('smsError').textContent = '请输入 11 位手机号';
    return;
  }
  $('btnSendCode').disabled = true;
  $('btnSendCode').textContent = '发送中…';

  const result = await post('sendCode', { phone });

  if (result.error) {
    $('smsError').textContent = result.error;
    $('btnSendCode').textContent = '获取验证码';
    $('btnSendCode').disabled = false;
    return;
  }

  state.verificationId = result.verificationId || '';
  state.isUser = result.isUser !== false;
  $('nickRow').hidden = state.isUser;
  startCountdown();
});

function startCountdown() {
  state.countdown = 60;
  const button = $('btnSendCode');
  button.textContent = state.countdown + 's 后重发';
  button.disabled = true;

  const timer = setInterval(() => {
    state.countdown -= 1;
    if (state.countdown <= 0) {
      clearInterval(timer);
      button.textContent = '获取验证码';
      button.disabled = $('phoneSms').value.trim().length !== 11;
    } else {
      button.textContent = state.countdown + 's 后重发';
    }
  }, 1000);
}

/* ── 登录 ─────────────────────────────── */

$('btnSmsLogin').addEventListener('click', async () => {
  const phone = $('phoneSms').value.trim();
  const code = $('smsCode').value.trim();

  if (phone.length !== 11) { $('smsError').textContent = '请输入 11 位手机号'; return; }
  if (!state.verificationId) { $('smsError').textContent = '请先获取验证码'; return; }
  if (code.length !== 6) { $('smsError').textContent = '请输入 6 位数字验证码'; return; }

  $('smsError').textContent = '';
  $('btnSmsLogin').disabled = true;
  $('btnSmsLogin').textContent = '正在登录…';

  const result = await post('verifyLogin', {
    phone,
    verificationId: state.verificationId,
    code,
    nickname: $('nickname').value.trim(),
    isUser: state.isUser ? '1' : '0'
  });

  $('btnSmsLogin').disabled = false;
  $('btnSmsLogin').textContent = '登录 / 注册';

  if (result.error) {
    $('smsError').textContent = result.error;
  } else {
    resetLoginForm();
    post('ready');
  }
});

$('btnPasswordLogin').addEventListener('click', async () => {
  const account = $('account').value.trim();
  const password = $('password').value;

  if (!account) { $('passwordError').textContent = '请输入账号'; return; }
  if (!password) { $('passwordError').textContent = '请输入密码'; return; }

  $('passwordError').textContent = '';
  $('btnPasswordLogin').disabled = true;
  $('btnPasswordLogin').textContent = '正在登录…';

  const result = await post('loginPassword', { account, password });

  $('btnPasswordLogin').disabled = false;
  $('btnPasswordLogin').textContent = '登录';

  if (result.error) {
    $('passwordError').textContent = result.error;
  } else {
    resetLoginForm();
    post('ready');
  }
});

function resetLoginForm() {
  $('phoneSms').value = '';
  $('smsCode').value = '';
  $('account').value = '';
  $('password').value = '';
  $('nickname').value = '';
  $('nickRow').hidden = true;
  $('smsError').textContent = '';
  $('passwordError').textContent = '';
  state.verificationId = '';
}

/* ── 初始化 ───────────────────────────── */

post('viewport', { width: window.innerWidth, height: window.innerHeight });
post('ready');
setInterval(() => post('ready'), 2000);
