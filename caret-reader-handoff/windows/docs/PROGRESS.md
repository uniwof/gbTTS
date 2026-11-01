# 第 4 轮进度（B 线 文本引擎）
- B1 Markdown 清理+标题停顿、B2 symbols.tsv：第 1 轮已在 dev（TextPipeline/MarkdownCleaner/SymbolTable），设置里可关。
- B3 Ctrl+Alt+D 引擎接口（给 A 线，不碰界面）：
  - `UserLexicon.NormalizeSource(selected)` → 去空白；空/多行/超过 30 字返回 null（界面提示“请只选一个词”）。
  - `UserLexicon.DefaultWholeWord(src)` → 英文数字默认整词；可作为小窗“整词”勾选的初值。
  - `UserLexicon.AddReading(UserRulesPath, selected, reading, wholeWord?)` → `AddReadingResult(Ok, Error, Replaced)`。reading 写汉字=替换规则；写带调拼音（如 yin2 hang2）=读音规则。同一原文覆盖旧行；失败不写文件；Error 是中文原因可直接显示。
  - `UserLexicon.PreviewText(pipeline, source)` → 试听前看会读成什么。
  - 用户词库路径：`Path.Combine(TextLayerSettings.UserDataDir(settingsPath), SettingsMigration.UserRulesFile)`；保存后 TextLayer.For 会按文件时间自动重载，不需重启。
- B4 golden.tsv 增加 Markdown 单行用例；UserLexiconTests 覆盖 B3。
