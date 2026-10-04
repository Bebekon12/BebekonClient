# JNI looks up generated class and member names. These must retain their exact names.
-keep class go.** { *; }
-keep class io.nekohasekai.libbox.** { *; }
-keep class org.snakeyaml.engine.v2.** { *; }
-keepattributes Signature,InnerClasses,EnclosingMethod,RuntimeVisibleAnnotations,AnnotationDefault
