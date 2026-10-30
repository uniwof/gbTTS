# v0.6 真机测试进度（断点续跑用）
- [10:55] 开始。读完记忆与更新说明。下一步：GitHub 建私有仓库 caret-reader-wintest。
- [11:00] 发现前一子代理留下的测试框架：/workspace/work/tts/v06test/（run.ps1 三路径×多目标、measure.ps1 资源、audio.ps1 声卡、w.cs Win32 帮助类、gha.yml/appveyor.yml、ntfy 话题 .ntfy_topic，结果经 temp.sh 回传）。b6/v06x 是旧 v0.6 exe（08:56），要换成 app/publish/win-x64 的 09:42 版。
- [11:00] GitHub 连接器仍不能建仓库（403 Resource not accessible by integration，与 10-13 相同）。AppVeyor API key 在 v06test/.appveyor_key（v2 key，需账号名）。改走浏览器：github.com 已保存登录建私有仓库 + 细粒度 PAT。
- [11:08] 浏览器无法登录 github.com（Hark 内置集成禁止网页登录）→ GitHub 不可行。改 AppVeyor：账号 EthanBrooks，Free 计划，0 项目；用 API 建 git 通用项目（octocat/Hello-World 占位，YAML 放 AppVeyor 设置里）。
- [12:25] 已问用户（建空私有仓库 caret-reader-wintest + 给 Hark GitHub 应用授权该仓库），60 分钟未答 → 本轮停止。复查：/user/installations 仍为 0。
## 断点续跑要点
1. 用户回"好了"后：确认 `curl https://api.github.com/user/repos`（shell credentials github）能看到 uniwof/caret-reader-wintest。
2. 把 /workspace/work/tts/v06test 的 run.ps1/measure.ps1/audio.ps1/w.cs/t.* /long.txt 放进仓库 .github/wintest/；程序：轻量版 zip（236MB）太大不进仓库 → 用 release asset 或 runner 上从源码 `dotnet publish`（app/），模型从 bacloud 公开路径下载；v06 exe 用 app/publish/win-x64/CaretReader.exe（09:42 版，不是 b6/v06x 旧版）。
3. gha.yml 去掉旧的 temp.sh 链接（已过期），v0.6 用 -Mode ball（小人/圆球都是正方形窗口），结果上传为 Actions artifact。
4. Word/WPS runner 没有 → 报告写"未测"。
