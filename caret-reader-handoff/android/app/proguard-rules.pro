# 不混淆、只去掉没用到的代码。
-dontobfuscate
-dontoptimize
# sherpa-onnx 的 JNI 会按名字读取这些类的字段 / 构造 GeneratedAudio，必须完整保留
-keep class com.k2fsa.sherpa.onnx.** { *; }
# 本应用的服务 / Activity 由系统按类名启动
-keep class app.caretreader.tts.** { *; }
-keepattributes *Annotation*,Signature,InnerClasses,EnclosingMethod
-dontwarn **
