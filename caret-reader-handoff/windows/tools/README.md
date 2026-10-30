# tools

## onnx2voicepack.py：朋友的 Piper onnx → 音色包

一句话用法：`python onnx2voicepack.py <朋友的模型文件夹> <输出文件夹> --name 显示名`，再把生成的子文件夹复制到朗读器的 `voices` 里。

- 先装依赖（都可商用）：`pip install onnx numpy pypinyin`
- 模型文件夹里要有一个 `.onnx` 和同名的 `.onnx.json`（Piper 导出时自带）。
- 支持：Piper 拼音版（推荐，如从 zh_CN xiao_ya / chaowen 微调的）；已经是 sherpa-onnx 格式的 VITS（文件夹里自带 tokens.txt）。
- 不支持时会用中文说明原因（espeak 版、多个 onnx 的大模型、只有 .ckpt/.pth 等），不会崩溃。
- 安全：永远不加载 .ckpt / .pth。
- 词典由 pypinyin 生成（单字 + 常用词组）；多音字由朗读器的多音字前端兜底（脚本会顺便生成 pua_map.txt）。
