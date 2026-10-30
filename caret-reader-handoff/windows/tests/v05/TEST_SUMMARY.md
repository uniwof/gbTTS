# v0.5 多音字前端 测试汇总（2026-10-14，沙盒 ARM Linux，2 核，CPU 被其他任务大量占用）
- 原有自测：split 59/59（v0.4 同为 59）、packtest 35/35、paths 8/8、nullguard 1/1；crash_v02 按设计段错误（复现 v0.2 的闪退）；pause（轻量版 Matcha）5/5；switch（标准版）通过，换模型后内存回落
- frontendtest 12/12（用户词库格式、调号、ü、总行修正、规则、变调、PUA 转换、生僻字、英文）
- C# 前端 vs Python 实验版：corpus.txt（57,931 字）+ rand.txt（89,554 字）逐字一致（work/polylex/integ/）
- 盲测（score_cs.py）：实验口径数据 1/5/9=15（与实验完全相同的 15 处）；发布数据（含 35 条修正）1/5/8=14
- 前端单独（fbench，taskset 2 核）：加载 0.66–0.78 s，3.3–4.0 ms/千字，进程峰值 118–127 MB
- abcpu（同进程开/关前端，CPU 秒/语音秒）：chaowen 0.187/0.189，matcha 0.253/0.245，zh-ll 0.909/0.948，melo 1.325/1.326
- 整程序峰值（synth/switch）：Melo 476–493 MB（v0.4 362），Matcha 350–363（266–272），zh-ll 344–351，chaowen 325–331（216–223）
- ab_results.txt：taskset 下 v0.4 / v0.5 墙钟对比，负载 2.7–6.6 波动，仅供参考
