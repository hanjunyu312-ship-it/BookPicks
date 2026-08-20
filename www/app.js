'use strict';

/* ================= 小工具 ================= */
const $ = (s) => document.querySelector(s);

/* Android 直连模式:仅安卓 WebView(UA 含 Android)加载本地 http://127.0.0.1 静态资源时,
   前端直接访问 Open Library 官方接口(均返回 Access-Control-Allow-Origin:*)。
   注意:桌面版 WebView2 也加载 http://127.0.0.1:端口 页面,必须用 UA 区分——
   否则桌面版会被误判为直连模式,绕过本地服务器,导致书名/简介/标签全部失去自动翻译 */
const IS_ANDROID = /Android/i.test(navigator.userAgent);
const DIRECT = IS_ANDROID && (location.protocol === 'file:' ||
  location.hostname === '127.0.0.1' || location.hostname === 'localhost');

function coverUrl(item, size = 'M') {
  const id = item.cover_i ?? item.cover_id;
  if (!id) return null;
  // 桌面版经本地服务器代理取回(带缓存);安卓版直连封面 CDN
  return DIRECT ? `https://covers.openlibrary.org/b/id/${id}-${size}.jpg` : `/api/cover/${id}/${size}.jpg`;
}

function authorOf(item) {
  const n = item.author_name;
  if (Array.isArray(n)) return n.join(', ');
  if (typeof n === 'string') return n;
  if (Array.isArray(item.authors)) return item.authors.map((a) => a.name).filter(Boolean).join(', ');
  return '';
}

function yearOf(item) {
  return String(item.first_publish_year ?? item.year ?? '');
}

function ratingStar(r) {
  if (!r || r <= 0) return '';
  const stars = '★'.repeat(Math.max(1, Math.min(5, Math.round(r))));
  return `${stars} ${r.toFixed(1)}`;
}

const normKey = (key) => String(key || '').replace(/^\/(works|books)\//, '');

/** 直连模式 URL 映射:本地 /api/* 路径 → Open Library 官方接口 */
function directUrl(path) {
  let m;
  if ((m = path.match(/^\/api\/trending\/(\w+)$/)))
    return `https://openlibrary.org/trending/${m[1]}.json?limit=50`;
  if ((m = path.match(/^\/api\/subjects\/([\w_-]+)\?(.+)$/)))
    return `https://openlibrary.org/subjects/${m[1]}.json?${m[2]}`;
  if ((m = path.match(/^\/api\/search\?q=([^&]+)&page=(\d+)&limit=(\d+)$/)))
    return `https://openlibrary.org/search.json?q=${m[1]}&page=${m[2]}&limit=${m[3]}`;
  if ((m = path.match(/^\/api\/work\/([\w-]+)$/)))
    return `https://openlibrary.org/works/${m[1]}.json`;
  if ((m = path.match(/^\/api\/ratings\/([\w-]+)$/)))
    return `https://openlibrary.org/works/${m[1]}/ratings.json`;
  if ((m = path.match(/^\/api\/authors\/([\w-]+)$/)))
    return `https://openlibrary.org/authors/${m[1]}.json`;
  return null;
}

/* 直连模式本地缓存:榜单/书库数据缓存在 localStorage,断网时自动回退显示 */
const CACHE_TTL = 6 * 60 * 60 * 1000; // 6 小时

function readCache(key) {
  try {
    const j = JSON.parse(localStorage.getItem(key) || 'null');
    if (j && j.ts && Date.now() - j.ts < CACHE_TTL) return j.data;
  } catch { /* 忽略 */ }
  return null;
}

function writeCache(key, data) {
  try { localStorage.setItem(key, JSON.stringify({ ts: Date.now(), data })); } catch { /* 忽略 */ }
}

async function api(path, options) {
  const url = DIRECT ? (directUrl(path) || path) : path;
  const cacheKey = DIRECT ? 'bp-cache-' + path : null;
  const cached = cacheKey ? readCache(cacheKey) : null;
  let res;
  try {
    res = await fetch(url, options);
  } catch (e) {
    if (cached) { toast('⚠ 网络不可用,显示本地缓存'); return cached; }
    throw e;
  }
  let data = null;
  try { data = await res.json(); } catch { data = null; }
  if (!res.ok) {
    if (cached) { toast('⚠ 网络不可用,显示本地缓存'); return cached; }
    const why = data && data.error;
    throw new Error(why === 'network'
      ? '无法连接数据源,请确认网络代理(Clash/VPN)已开启,应用会自动重试'
      : why === 'not_found' ? '没有找到这本书'
      : `请求失败(${res.status})`);
  }
  // 直连模式数据形状兼容:搜索 / 分类接口返回结构与本地代理不同
  if (DIRECT) {
    if (path.startsWith('/api/search') && data && Array.isArray(data.docs))
      data = { works: data.docs, total: data.numFound };
    else if (path.startsWith('/api/subjects') && data && Array.isArray(data.works) && data.total == null)
      data.total = data.work_count || data.works.length;
  }
  if (cacheKey) writeCache(cacheKey, data);
  return data;
}

function extractDesc(d) {
  if (!d) return '';
  const text = typeof d === 'string' ? d : (d.value || '');
  return text.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
}

let toastTimer = null;
function toast(msg) {
  const t = $('#toast');
  t.textContent = msg;
  t.classList.remove('hidden');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.add('hidden'), 3200);
}

/* ================= 状态 ================= */
/* 书库分类:按主题分组展示(slug 为 Open Library subjects 接口的路径参数,均已验证有数据) */
const CATEGORY_GROUPS = [
  { group: '📖 文学', items: [
    ['fiction', '小说'], ['fantasy', '奇幻'], ['science_fiction', '科幻'],
    ['mystery', '悬疑'], ['romance', '爱情'], ['poetry', '诗歌'],
  ]},
  { group: '🌍 人文', items: [
    ['history', '历史'], ['biography', '传记'], ['philosophy', '哲学'],
    ['sociology', '社会学'], ['political_science', '政治'], ['anthropology', '人类学'],
    ['linguistics', '语言学'], ['archaeology', '考古'], ['law', '法律'],
    ['education', '教育'], ['religion', '宗教'],
  ]},
  { group: '🔬 基础科学', items: [
    ['science', '科学'], ['physics', '物理'], ['chemistry', '化学'],
    ['biology', '生物'], ['mathematics', '数学'], ['astronomy', '天文'],
    ['cosmology', '宇宙学'],
  ]},
  { group: '🧬 生命健康', items: [
    ['neuroscience', '脑科学'], ['psychology', '心理学'], ['genetics', '遗传学'],
    ['evolution', '进化论'], ['medicine', '医学'], ['health', '健康'],
    ['nutrition', '营养'], ['zoology', '动物学'], ['botany', '植物学'],
  ]},
  { group: '🌏 地球环境', items: [
    ['geology', '地质'], ['ecology', '生态'], ['environment', '环境'],
    ['climate', '气候'], ['earth_sciences', '地球科学'], ['energy', '能源'],
  ]},
  { group: '🤖 科技前沿', items: [
    ['computer_science', '计算机'], ['programming', '编程'], ['algorithms', '算法'],
    ['artificial_intelligence', '人工智能'], ['robotics', '机器人'],
    ['technology', '技术'], ['engineering', '工程'], ['space', '太空'],
  ]},
  { group: '💼 商学', items: [
    ['business', '商业'], ['economics', '经济学'], ['finance', '金融'],
    ['management', '管理'], ['marketing', '营销'], ['leadership', '领导力'],
  ]},
  { group: '🎨 艺术', items: [
    ['music', '音乐'], ['art', '艺术'], ['photography', '摄影'],
    ['design', '设计'], ['architecture', '建筑'],
  ]},
  { group: '🍳 生活', items: [
    ['cooking', '烹饪'], ['sports', '体育'], ['travel', '旅行'],
    ['parenting', '育儿'], ['spirituality', '灵修'],
    ['self_help', '自我提升'], ['mental_health', '心理健康'],
  ]},
];
const CATEGORIES = CATEGORY_GROUPS.flatMap((g) => g.items);

const state = {
  page: 'trending',
  trending: null,
  trendingPeriod: 'daily',
  category: 'fiction',
  search: '',
  pageNum: 1,
  pageSize: 24,
  favorites: [],
  dark: localStorage.getItem('bp-dark') === '1',
};

/* 榜单周期:slug → (中文名, Hero 标题, 副文案) */
const TRENDING_PERIODS = {
  daily: ['日榜', '今日全球热榜', '全球今天正在流行的书,每天更新。'],
  weekly: ['周榜', '本周全球热榜', '过去一周全球最受欢迎的书。'],
  monthly: ['月榜', '本月全球热榜', '过去一个月全球持续热门的书。'],
};

/* ================= 基础组件 ================= */
function spinner() {
  const d = document.createElement('div');
  d.className = 'spinner-wrap';
  d.innerHTML = '<div class="spinner"></div><p>加载中…</p>';
  return d;
}

/** 骨架屏:书籍网格加载占位(比 spinner 更有高级感) */
function skeletonGrid(n = 18) {
  const frag = document.createDocumentFragment();
  for (let i = 0; i < n; i++) {
    const card = document.createElement('div');
    card.className = 'sk-card';
    const cover = document.createElement('div');
    cover.className = 'sk-cover';
    const l1 = document.createElement('div');
    l1.className = 'sk-line w60';
    const l2 = document.createElement('div');
    l2.className = 'sk-line w40';
    card.append(cover, l1, l2);
    frag.appendChild(card);
  }
  return frag;
}


function errorBox(msg) {
  const d = document.createElement('div');
  d.className = 'error-box';
  const p = document.createElement('p');
  p.textContent = '😥 ' + msg;
  const btn = document.createElement('button');
  btn.className = 'btn';
  btn.textContent = '重新加载';
  btn.addEventListener('click', () => render());
  d.append(p, btn);
  // 自动重试:每 15 秒静默重试一次,直到成功(元素被替换即停止)
  const timer = setInterval(() => {
    if (!document.body.contains(d)) { clearInterval(timer); return; }
    render();
  }, 15000);
  return d;
}

function coverBox(item, size = 'M', cls = '') {
  const box = document.createElement('div');
  box.className = 'cover ' + cls;
  const url = coverUrl(item, size);
  if (url) {
    const img = document.createElement('img');
    img.loading = 'lazy';
    img.src = url;
    img.alt = item.title || '书籍封面';
    img.onerror = () => { box.classList.add('cover-fallback'); box.textContent = '📕'; };
    box.appendChild(img);
  } else {
    box.classList.add('cover-fallback');
    box.textContent = '📕';
  }
  return box;
}

function bookCard(item, rank) {
  const card = document.createElement('article');
  card.className = 'book-card';
  card.title = (item.title || '') + (authorOf(item) ? ' — ' + authorOf(item) : '');
  const inner = document.createElement('div');
  inner.className = 'card-inner';
  if (rank) {
    const r = document.createElement('span');
    r.className = 'rank' + (rank <= 3 ? ' rank-top rank-' + rank : '');
    r.textContent = rank;
    inner.appendChild(r);
  }
  inner.appendChild(coverBox(item, 'M'));
  // 评分徽章(封面左下角)
  const ba = Number(item.ratings_average);
  if (ba > 0) {
    const b = document.createElement('span');
    b.className = 'cover-badge';
    b.textContent = '★ ' + ba.toFixed(1);
    inner.appendChild(b);
  }
  card.appendChild(inner);
  const meta = document.createElement('div');
  meta.className = 'book-meta';
  const t = document.createElement('h3');
  t.className = 'book-title';
  t.textContent = item.title || '未知书名';
  const a = document.createElement('p');
  a.className = 'book-author';
  a.textContent = authorOf(item) || '佚名';
  meta.append(t, a);
  if (item.title_original && item.title_original !== item.title) {
    const o = document.createElement('p');
    o.className = 'book-title-orig';
    o.textContent = item.title_original;
    meta.append(o);
  }
  card.appendChild(meta);
  card.addEventListener('click', () => openDetail(item));
  // 液态玻璃光随指动:更新卡片高光跟随鼠标的位置
  card.addEventListener('mousemove', (e) => {
    const r = card.getBoundingClientRect();
    card.style.setProperty('--mx', ((e.clientX - r.left) / r.width * 100) + '%');
    card.style.setProperty('--my', ((e.clientY - r.top) / r.height * 100) + '%');
  });
  return card;
}

function renderGridInto(grid, cards) {
  // 交错入场:每张卡片延迟 30ms,形成涟漪效果
  cards.forEach((card, i) => { card.style.animationDelay = Math.min(i * 30, 420) + 'ms'; });
  grid.replaceChildren(...cards);
}

/* ================= 导航 ================= */
function gotoPage(p) {
  state.page = p;
  document.querySelectorAll('.tab').forEach((t) => {
    t.classList.toggle('active', t.dataset.page === p);
  });
  render();
}

async function render() {
  const c = $('#content');
  c.innerHTML = '';
  // 页面切换过渡动画
  c.classList.remove('page-in');
  void c.offsetWidth;
  c.classList.add('page-in');
  try {
    if (state.page === 'trending') await renderTrending(c);
    else if (state.page === 'library') await renderLibrary(c);
    else if (state.page === 'pick') await renderPick(c);
    else await renderFavorites(c);
  } catch (e) {
    c.innerHTML = '';
    c.appendChild(errorBox(e.message || '出了点问题'));
  }
}

/* ================= 今日全球热榜 ================= */
async function renderTrending(c) {
  const period = () => TRENDING_PERIODS[state.trendingPeriod] || TRENDING_PERIODS.daily;

  // Hero 横幅:日期 + 渐变大标题 + 日/周/月切换
  const hero = document.createElement('section');
  hero.className = 'hero';
  const heroDate = document.createElement('p');
  heroDate.className = 'hero-date';
  try {
    heroDate.textContent = new Date().toLocaleDateString('zh-CN',
      { year: 'numeric', month: 'long', day: 'numeric', weekday: 'long' });
  } catch { heroDate.textContent = '今日热榜'; }
  const heroTitle = document.createElement('h2');
  heroTitle.className = 'hero-title';
  heroTitle.textContent = period()[1];
  const heroSub = document.createElement('p');
  heroSub.className = 'hero-sub';
  heroSub.textContent = period()[2];
  hero.append(heroDate, heroTitle, heroSub);

  // Hero 装饰:顶部金色光条 + 漂浮书本粒子
  const topline = document.createElement('div');
  topline.className = 'hero-topline';
  hero.appendChild(topline);
  const sparks = document.createElement('div');
  sparks.className = 'hero-sparks';
  ['📚', '📖', '✨'].forEach((t) => {
    const s = document.createElement('span');
    s.textContent = t;
    sparks.appendChild(s);
  });
  hero.appendChild(sparks);

  const periodTabs = document.createElement('div');
  periodTabs.className = 'period-tabs';
  Object.entries(TRENDING_PERIODS).forEach(([slug, [label]]) => {
    const b = document.createElement('button');
    b.className = 'period-tab' + (slug === state.trendingPeriod ? ' active' : '');
    b.textContent = label;
    b.addEventListener('click', () => {
      if (state.trendingPeriod === slug) return;
      state.trendingPeriod = slug;
      load();
    });
    periodTabs.appendChild(b);
  });
  hero.appendChild(periodTabs);
  c.appendChild(hero);

  const head = document.createElement('div');
  head.className = 'section-head';
  const htxt = document.createElement('div');
  const h2 = document.createElement('h2');
  h2.textContent = '全球趋势榜';
  const hint = document.createElement('span');
  hint.className = 'hint';
  hint.textContent = 'TOP 100 · 数据来自 Open Library 全球趋势';
  htxt.append(h2, hint);
  const refresh = document.createElement('button');
  refresh.className = 'btn ghost';
  refresh.textContent = '🔄 刷新榜单';
  head.append(htxt, refresh);
  c.appendChild(head);

  const grid = document.createElement('div');
  grid.className = 'book-grid';
  c.appendChild(grid);

  const load = async () => {
    const p = state.trendingPeriod;
    heroTitle.textContent = period()[1];
    heroSub.textContent = period()[2];
    document.querySelectorAll('.period-tab').forEach((t, i) =>
      t.classList.toggle('active', Object.keys(TRENDING_PERIODS)[i] === p));
    grid.innerHTML = '';
    grid.appendChild(skeletonGrid(24));
    try {
      const data = await api(`/api/trending/${p}`);
      if (data.cached) toast('⚠ 网络不可用,当前显示本地缓存榜单');
      state.trending = data.works || [];
      if (!state.trending.length) throw new Error('榜单为空,请稍后再试');
      renderGridInto(grid, state.trending.map((w, i) => bookCard(w, i + 1)));
    } catch (e) {
      grid.innerHTML = '';
      grid.appendChild(errorBox(e.message));
    }
  };
  refresh.addEventListener('click', load);
  await load();
}

/* ================= 书库(分类 + 搜索) ================= */
async function renderLibrary(c) {
  const head = document.createElement('div');
  head.className = 'section-head';
  const htxt = document.createElement('div');
  const h2 = document.createElement('h2');
  h2.textContent = '书库';
  const hint = document.createElement('span');
  hint.className = 'hint';
  hint.textContent = '按分类浏览,或搜索书名 / 作者';
  htxt.append(h2, hint);
  head.appendChild(htxt);
  c.appendChild(head);

  const groupsWrap = document.createElement('div');
  groupsWrap.className = 'chip-groups';
  CATEGORY_GROUPS.forEach((g) => {
    const gl = document.createElement('p');
    gl.className = 'chip-group';
    gl.textContent = g.group;
    groupsWrap.appendChild(gl);
    const chips = document.createElement('div');
    chips.className = 'chips';
    g.items.forEach(([slug, label]) => {
      const b = document.createElement('button');
      b.className = 'chip' + (slug === state.category ? ' active' : '');
      b.dataset.slug = slug;
      b.textContent = label;
      b.addEventListener('click', () => {
        state.category = slug;
        state.pageNum = 1;
        state.search = '';
        input.value = '';
        refreshChips();
        loadPage();
      });
      chips.appendChild(b);
    });
    groupsWrap.appendChild(chips);
  });
  c.appendChild(groupsWrap);

  const bar = document.createElement('div');
  bar.className = 'search-bar';
  const input = document.createElement('input');
  input.type = 'text';
  input.placeholder = '输入书名或作者,如:Harry Potter / Tolkien';
  const btn = document.createElement('button');
  btn.className = 'btn';
  btn.textContent = '搜索';
  bar.append(input, btn);
  c.appendChild(bar);

  const grid = document.createElement('div');
  grid.className = 'book-grid';
  c.appendChild(grid);
  const pager = document.createElement('div');
  pager.className = 'pager';
  c.appendChild(pager);

  const refreshChips = () =>
    document.querySelectorAll('.chip').forEach((ch) =>
      ch.classList.toggle('active', ch.dataset.slug === state.category));

  const doSearch = () => {
    state.search = input.value.trim();
    state.pageNum = 1;
    loadPage();
  };
  btn.addEventListener('click', doSearch);
  input.addEventListener('keydown', (ev) => { if (ev.key === 'Enter') doSearch(); });

  const loadPage = async () => {
    grid.innerHTML = '';
    grid.appendChild(skeletonGrid(18));
    pager.innerHTML = '';
    try {
      const q = state.search;
      const data = q
        ? await api(`/api/search?q=${encodeURIComponent(q)}&page=${state.pageNum}&limit=${state.pageSize}`)
        : await api(`/api/subjects/${state.category}?offset=${(state.pageNum - 1) * state.pageSize}&limit=${state.pageSize}`);
      if (data.cached) toast('⚠ 网络不可用,当前显示本地缓存');
      const works = data.works || [];
      renderGridInto(grid, works.map((w) => bookCard(w)));
      if (!works.length) {
        const empty = document.createElement('p');
        empty.className = 'empty';
        empty.textContent = q ? `没有找到与「${q}」相关的书,换个关键词试试?` : '这个分类暂时没有书。';
        grid.appendChild(empty);
      }
      const total = Math.max(data.total || works.length, works.length);
      const pages = Math.max(1, Math.ceil(total / state.pageSize));
      const info = document.createElement('span');
      info.className = 'pager-info';
      info.textContent = `第 ${state.pageNum} / ${pages} 页 · 共约 ${total} 本`;
      const prev = document.createElement('button');
      prev.className = 'btn ghost';
      prev.textContent = '← 上一页';
      prev.disabled = state.pageNum <= 1;
      prev.addEventListener('click', () => { state.pageNum--; loadPage(); });
      const next = document.createElement('button');
      next.className = 'btn ghost';
      next.textContent = '下一页 →';
      next.disabled = state.pageNum >= pages;
      next.addEventListener('click', () => { state.pageNum++; loadPage(); });
      pager.append(prev, info, next);
    } catch (e) {
      grid.innerHTML = '';
      grid.appendChild(errorBox(e.message));
    }
  };

  await loadPage();
}

/* ================= 帮我选书(随机推荐) ================= */
function renderPick(c) {
  const head = document.createElement('div');
  head.className = 'section-head';
  const htxt = document.createElement('div');
  const h2 = document.createElement('h2');
  h2.textContent = '帮我选书';
  const hint = document.createElement('span');
  hint.className = 'hint';
  hint.textContent = '纠结不知道看什么?让命运替你翻牌 🎲';
  htxt.append(h2, hint);
  head.appendChild(htxt);
  c.appendChild(head);

  const panel = document.createElement('div');
  panel.className = 'pick-panel';
  const label = document.createElement('label');
  label.className = 'pick-sel';
  label.textContent = '推荐范围:';
  const sel = document.createElement('select');
  const optAll = document.createElement('option');
  optAll.value = 'all';
  optAll.textContent = '今日全球热榜';
  sel.appendChild(optAll);
  CATEGORIES.forEach(([slug, label2]) => {
    const o = document.createElement('option');
    o.value = slug;
    o.textContent = label2;
    sel.appendChild(o);
  });
  label.appendChild(sel);
  const btn = document.createElement('button');
  btn.className = 'btn big';
  btn.textContent = '🎲 随便看看';
  panel.append(label, btn);
  c.appendChild(panel);

  const result = document.createElement('div');
  result.className = 'pick-result';
  c.appendChild(result);

  let pickFn = async () => {};
  pickFn = async () => {
    result.innerHTML = '';
    result.appendChild(spinner());
    try {
      const scope = sel.value;
      let pool = null;
      if (scope === 'all') {
        if (!state.trending) {
          const data = await api('/api/trending');
          state.trending = data.works || [];
        }
        pool = state.trending;
      } else {
        const page = 1 + Math.floor(Math.random() * 4);
        const data = await api(`/api/subjects/${scope}?offset=${(page - 1) * 24}&limit=24`);
        pool = data.works || [];
      }
      if (!pool || !pool.length) throw new Error('这个范围暂时没有书,换个分类试试');
      // 随机抽 3 本(去重)
      const picks = [];
      const tried = new Set();
      while (picks.length < 3 && tried.size < Math.min(pool.length, 30)) {
        const pick = pool[Math.floor(Math.random() * pool.length)];
        const k = pick.key || pick.title;
        if (tried.has(k)) continue;
        tried.add(k);
        picks.push(pick);
      }
      await renderPickResult(result, picks);
    } catch (e) {
      result.innerHTML = '';
      result.appendChild(errorBox(e.message));
    }
  };

  btn.addEventListener('click', pickFn);
  sel.addEventListener('change', pickFn);
  pickFn();
}

async function renderPickResult(result, items) {
  result.innerHTML = '';
  const grid = document.createElement('div');
  grid.className = 'pick-grid';
  result.appendChild(grid);
  items.forEach((item, idx) => {
    const card = document.createElement('div');
    card.className = 'pick-card';
    card.style.animationDelay = (idx * 90) + 'ms';
    card.appendChild(coverBox(item, 'L', 'pick-cover'));

    const info = document.createElement('div');
    info.className = 'pick-info';
    const title = document.createElement('h3');
    title.className = 'pick-title';
    title.textContent = item.title || '未知书名';
    info.appendChild(title);
    if (item.title_original && item.title_original !== item.title) {
      const o = document.createElement('p');
      o.className = 'pick-title-orig';
      o.textContent = item.title_original;
      info.appendChild(o);
    }
    const meta = document.createElement('p');
    meta.className = 'pick-meta';
    meta.textContent = [authorOf(item), yearOf(item), ratingStar(item.ratings_average)]
      .filter(Boolean).join(' · ') || '作者不详';
    info.appendChild(meta);
    const snippet = document.createElement('p');
    snippet.className = 'pick-snippet';
    snippet.textContent = '正在加载简介…';
    info.appendChild(snippet);
    const actions = document.createElement('div');
    actions.className = 'pick-actions';
    const detailBtn = document.createElement('button');
    detailBtn.className = 'btn';
    detailBtn.textContent = '查看完整详情';
    detailBtn.addEventListener('click', () => openDetail(item));
    actions.appendChild(detailBtn);
    info.appendChild(actions);
    card.appendChild(info);
    grid.appendChild(card);

    // 简介异步加载
    (async () => {
      try {
        const data = await api(`/api/work/${normKey(item.key)}`);
        const wd = data.cached === true ? data.data : data;
        const d = extractDesc(wd && wd.description);
        snippet.textContent = d
          ? (d.length > 200 ? d.slice(0, 200) + '…' : d)
          : '这本书暂时没有简介,点「查看完整详情」看看更多信息。';
      } catch {
        snippet.textContent = '简介加载失败,点「查看完整详情」试试。';
      }
    })();
  });
}

/* ================= 收藏 ================= */
/* 直连模式(安卓)收藏存 localStorage;桌面版走本地服务器文件 */
function favsLoad() {
  try { return JSON.parse(localStorage.getItem('bp-favs') || '[]'); } catch { return []; }
}

function favsSave(list) {
  try { localStorage.setItem('bp-favs', JSON.stringify(list)); } catch { /* 忽略 */ }
}

async function loadFavs() {
  if (DIRECT) { state.favorites = favsLoad(); return; }
  try {
    const data = await api('/api/favorites');
    state.favorites = Array.isArray(data) ? data : [];
  } catch {
    state.favorites = [];
  }
}

async function persistFavs() {
  if (DIRECT) { favsSave(state.favorites); return; }
  try {
    await fetch('/api/favorites', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(state.favorites),
    });
  } catch { /* 忽略 */ }
}

function isFav(key) {
  return state.favorites.some((f) => f.key === normKey(key));
}

function toggleFav(item) {
  const key = normKey(item.key);
  const i = state.favorites.findIndex((f) => f.key === key);
  if (i >= 0) {
    state.favorites.splice(i, 1);
    toast('已取消收藏');
  } else {
    state.favorites.push({
      key,
      title: item.title || '未知书名',
      author: authorOf(item),
      cover_i: item.cover_i ?? item.cover_id ?? null,
      year: item.first_publish_year ?? null,
      addedAt: Date.now(),
    });
    toast('❤ 已加入收藏');
  }
  persistFavs();
}

async function renderFavorites(c) {
  const head = document.createElement('div');
  head.className = 'section-head';
  const htxt = document.createElement('div');
  const h2 = document.createElement('h2');
  h2.textContent = '我的收藏';
  const hint = document.createElement('span');
  hint.className = 'hint';
  hint.textContent = `共 ${state.favorites.length} 本 · 保存在本机`;
  htxt.append(h2, hint);
  head.appendChild(htxt);
  c.appendChild(head);

  const grid = document.createElement('div');
  grid.className = 'book-grid';
  c.appendChild(grid);

  if (!state.favorites.length) {
    const empty = document.createElement('div');
    empty.className = 'empty';
    const p = document.createElement('p');
    p.textContent = '还没有收藏任何书。去「今日热榜」或「书库」逛逛,点开一本书,点 ♥ 收藏吧。';
    const go = document.createElement('button');
    go.className = 'btn';
    go.textContent = '去今日热榜逛逛 →';
    go.addEventListener('click', () => gotoPage('trending'));
    empty.append(p, go);
    grid.appendChild(empty);
    return;
  }

  renderGridInto(grid, state.favorites.map((f) => {
    const card = bookCard(f);
    const rm = document.createElement('button');
    rm.className = 'btn tiny danger';
    rm.textContent = '移除';
    rm.addEventListener('click', (e) => {
      e.stopPropagation();
      toggleFav(f);
      render();
    });
    card.appendChild(rm);
    return card;
  }));
}

/* ================= 详情弹窗 ================= */
async function openDetail(item) {
  const modal = $('#modal');
  const body = $('#modalBody');
  modal.classList.remove('hidden');
  body.innerHTML = '';
  body.appendChild(spinner());

  let work = null;
  let rating = null;
  let ratingCounts = null;
  let authorName = '';
  try {
    const key = normKey(item.key);
    // 详情与评分并行请求,作者信息在详情返回后立刻发出,弹窗打开更快
    const dataPromise = api(`/api/work/${key}`).catch(() => null);
    const rrPromise = api(`/api/ratings/${key}`).catch(() => null);
    const data = await dataPromise;
    if (data) {
      work = data.cached === true ? data.data : data;
      if (data.cached === true) toast('⚠ 网络不可用,详情为本地缓存');
      if (work && work.authors && work.authors.length) {
        const ak = String(work.authors[0].key || '').replace('/authors/', '');
        if (ak) {
          const ar = await api(`/api/authors/${ak}`).catch(() => null);
          if (ar) authorName = ar.name || '';
        }
      }
    }
    const rr = await rrPromise;
    if (rr && rr.summary) { rating = rr.summary; ratingCounts = rr.counts; }
  } catch { /* 网络异常时用列表里的信息兜底展示 */ }

  const title = (work && work.title) || item.title || '未知书名';
  const author = authorName || authorOf(item) || '佚名';
  const year = (work && (work.first_publish_date || work.first_publish_year)) || item.first_publish_year || '';
  const desc = extractDesc(work && work.description);
  const subjects = Array.isArray(work && work.subjects) ? work.subjects.slice(0, 12) : [];
  const covers = Array.isArray(work && work.covers) ? work.covers : [];
  const coverId = item.cover_i ?? item.cover_id ?? covers[0];

  body.innerHTML = '';
  const detail = document.createElement('div');
  detail.className = 'detail';

  const left = document.createElement('div');
  left.className = 'detail-cover-wrap';
  const cover = document.createElement('div');
  cover.className = 'cover detail-cover';
  if (coverId) {
    const img = document.createElement('img');
    img.src = DIRECT ? `https://covers.openlibrary.org/b/id/${coverId}-L.jpg` : `/api/cover/${coverId}/L.jpg`;
    img.alt = title;
    img.onerror = () => { cover.classList.add('cover-fallback'); cover.textContent = '📕'; };
    cover.appendChild(img);
  } else {
    cover.classList.add('cover-fallback');
    cover.textContent = '📕';
  }
  left.appendChild(cover);
  const favBtn = document.createElement('button');
  favBtn.className = 'btn' + (isFav(item.key) ? ' faved' : '');
  favBtn.textContent = isFav(item.key) ? '♥ 已收藏' : '♡ 收藏这本书';
  favBtn.addEventListener('click', () => {
    toggleFav(item);
    favBtn.classList.toggle('faved', isFav(item.key));
    favBtn.textContent = isFav(item.key) ? '♥ 已收藏' : '♡ 收藏这本书';
    // 心跳动画
    favBtn.classList.remove('pop');
    void favBtn.offsetWidth;
    favBtn.classList.add('pop');
  });
  left.appendChild(favBtn);
  detail.appendChild(left);

  const right = document.createElement('div');
  right.className = 'detail-info';
  const h = document.createElement('h2');
  h.textContent = title;
  right.appendChild(h);
  const origTitle = extractDesc(work && work.title_original) || (item.title_original !== item.title ? item.title_original : '');
  if (origTitle) {
    const ot = document.createElement('p');
    ot.className = 'detail-title-orig';
    ot.textContent = origTitle;
    right.appendChild(ot);
  }
  const meta = document.createElement('p');
  meta.className = 'detail-meta';
  meta.textContent = [author, year ? String(year) : ''].filter(Boolean).join(' · ');
  right.appendChild(meta);

  // 星级评分条:按平均分部分填充(高级细节)
  if (rating && Number(rating.average) > 0) {
    const row = document.createElement('div');
    row.className = 'detail-meta';
    const bar = document.createElement('span');
    bar.className = 'rating-bar';
    const back = document.createElement('span');
    back.className = 'stars-back';
    back.textContent = '★★★★★';
    const fill = document.createElement('span');
    fill.className = 'stars-fill';
    fill.textContent = '★★★★★';
    fill.style.width = Math.max(0, Math.min(100, rating.average / 5 * 100)) + '%';
    bar.append(back, fill);
    const txt = document.createElement('span');
    txt.className = 'rating-text';
    txt.textContent = `${Number(rating.average).toFixed(1)} · ${rating.count ?? 0} 人评分`;
    row.append(bar, txt);
    right.appendChild(row);
  }

  const dh = document.createElement('h4');
  dh.textContent = '简介';
  right.appendChild(dh);
  const dp = document.createElement('p');
  dp.className = 'detail-desc';
  dp.textContent = desc || '本书暂无简介。';
  right.appendChild(dp);
  // 简介已自动翻译为中文,可切换查看英文原文
  const descOrig = extractDesc(work && work.description_original);
  if (descOrig) {
    const tg = document.createElement('button');
    tg.className = 'btn tiny ghost';
    tg.textContent = '查看英文原文';
    tg.addEventListener('click', () => {
      const showingZh = dp.textContent === desc;
      dp.textContent = showingZh ? descOrig : desc;
      tg.textContent = showingZh ? '回到中文' : '查看英文原文';
    });
    right.appendChild(tg);
  }

  if (subjects.length) {
    const sh = document.createElement('h4');
    sh.textContent = '主题标签';
    right.appendChild(sh);
    const sWrap = document.createElement('div');
    sWrap.className = 'chips';
    const subjectsOrig = Array.isArray(work && work.subjects_original) ? work.subjects_original : [];
    const zhSubjects = subjectsOrig.length === subjects.length ? subjectsOrig : null;
    subjects.forEach((s, i) => {
      const sp = document.createElement('span');
      sp.className = 'chip static';
      sp.textContent = s;
      // 悬停显示原文(标签已自动翻译为中文)
      sp.title = (zhSubjects && zhSubjects[i] !== s) ? zhSubjects[i] : s;
      sWrap.appendChild(sp);
    });
    right.appendChild(sWrap);
    // 标签已翻译时,可切换查看英文原文(与简介的切换逻辑一致)
    if (zhSubjects) {
      const tg = document.createElement('button');
      tg.className = 'btn tiny ghost';
      tg.textContent = '查看英文标签';
      tg.addEventListener('click', () => {
        const showingZh = tg.textContent === '查看英文标签';
        sWrap.querySelectorAll('.chip.static').forEach((c, i) => {
          c.textContent = showingZh ? subjectsOrig[i] : subjects[i];
          c.title = showingZh ? subjects[i] : subjectsOrig[i];
        });
        tg.textContent = showingZh ? '回到中文' : '查看英文标签';
      });
      right.appendChild(tg);
    }
  }
  const src = document.createElement('p');
  src.className = 'detail-src';
  src.textContent = '数据来源:Open Library(openlibrary.org)';
  right.appendChild(src);
  detail.appendChild(right);
  body.appendChild(detail);
  body.scrollTop = 0;
}

function closeModal() {
  $('#modal').classList.add('hidden');
  $('#modalBody').innerHTML = '';
}

/* ================= 翻译引擎(桌面版) ================= */
/* 本地离线引擎状态渲染 + 一键下载安装;未安装时点击 footer 状态弹出安装窗口 */
const TRANS_STATUS_TEXT = {
  ready: '本地离线翻译引擎已就绪',
  starting: '本地翻译引擎启动中…',
  installing: '正在安装离线翻译模型…',
  not_installed: '在线翻译 · 点击下载免费离线模型',
  failed: '在线翻译 · 离线引擎异常,点击重试',
};

function renderTransStatus(ts) {
  const el = $('#transStatus');
  if (!el) return;
  const action = ts.status === 'not_installed' || ts.status === 'failed';
  el.textContent = '🌐 ' + (TRANS_STATUS_TEXT[ts.status] || ('翻译引擎:' + ts.status));
  el.classList.toggle('action', action);
  el.onclick = action ? () => installTranslator() : null;
}

async function installTranslator() {
  try {
    const r = await api('/api/translate/install', { method: 'POST' });
    if (!r.ok) { toast('⚠ ' + (r.error || '启动安装失败')); return; }
    if (r.already) { toast('离线模型已安装'); renderTransStatus({ status: 'ready' }); return; }
    toast('开始安装,请留意弹出的安装窗口(约需几分钟)');
    renderTransStatus({ status: 'installing' });
    // 轮询安装结果:最多 10 分钟
    for (let i = 0; i < 120; i++) {
      await new Promise((r2) => setTimeout(r2, 5000));
      const t2 = await api('/api/translate/status').catch(() => null);
      if (!t2) continue;
      renderTransStatus(t2);
      if (t2.status === 'ready' || t2.status === 'failed') break;
    }
  } catch {
    toast('安装启动失败');
  }
}

/* ================= 主题 ================= */
function applyTheme() {
  document.documentElement.dataset.theme = state.dark ? 'dark' : 'light';
  $('#themeToggle').textContent = state.dark ? '☀️' : '🌙';
}

/* ================= 启动 ================= */
(async function init() {
  applyTheme();
  // 全局点击波纹(事件委托,覆盖所有动态生成的按钮)
  document.addEventListener('click', (e) => {
    const btn = e.target.closest('.btn');
    if (!btn) return;
    const r = btn.getBoundingClientRect();
    const size = Math.max(r.width, r.height);
    const span = document.createElement('span');
    span.className = 'ripple';
    span.style.width = span.style.height = size + 'px';
    span.style.left = (e.clientX - r.left - size / 2) + 'px';
    span.style.top = (e.clientY - r.top - size / 2) + 'px';
    btn.appendChild(span);
    setTimeout(() => span.remove(), 600);
  });
  $('#themeToggle').addEventListener('click', () => {
    state.dark = !state.dark;
    localStorage.setItem('bp-dark', state.dark ? '1' : '0');
    applyTheme();
  });
  $('#modalClose').addEventListener('click', closeModal);
  $('#modal').addEventListener('click', (e) => { if (e.target === $('#modal')) closeModal(); });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeModal(); });
  document.querySelectorAll('.tab').forEach((t) => t.addEventListener('click', () => gotoPage(t.dataset.page)));
  // 翻译引擎状态(桌面版;安卓直连模式无本地服务器)
  if (!DIRECT) {
    try {
      renderTransStatus(await api('/api/translate/status'));
    } catch { /* 服务器未就绪,忽略 */ }
  }
  await loadFavs();
  await render();
})();
