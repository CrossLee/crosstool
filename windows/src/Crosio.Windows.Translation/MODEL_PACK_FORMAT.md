# Crosio Windows 离线翻译模型包

## 系统能力结论

截至 2026-09-06，Microsoft 的 Windows AI API 总览仍把 Live Translation 标为
“Not yet supported”。Windows 11 没有一个面向普通桌面应用、可等价替代 Apple
Translation 的公开本机文本翻译 API。因此 Crosio 不调用未公开的系统接口，也不以
云翻译兜底。

参考：<https://learn.microsoft.com/windows/ai/apis/>

## 运行边界

- 翻译文字只进入本进程中的 SentencePiece 和 ONNX Runtime，不写入 HTTP 请求。
- 唯一网络操作是用户明确点击官方模型下载时拉取固定 revision 的模型权重与分词文件；也可完全离线选择本地 ZIP 安装。下载请求为无正文的 HTTPS GET，完整许可文本由 Crosio 的固定内置载荷写入模型包，不另行联网获取。
- 第一次下载并通过校验后，翻译可在断网状态下完成。
- 没有对应方向的有效模型时，引擎抛出 `TranslationModelMissingException`，不得返回
  占位文字或假翻译。

## 官方一键下载目录

`OfficialTranslationModelInstaller` 只接受翻译方向，不接受调用方提供的下载地址。
它从代码中固定的 Hugging Face revision 下载模型权重与分词文件，逐项检查 HTTPS 最终地址、
精确字节数和 SHA-256；许可文本从应用内固定载荷读取并执行相同的长度和 SHA-256 校验。所有文件通过校验后，安装器在私有临时目录中以
`CompressionLevel.NoCompression` 流式生成 ZIP，计算刚生成 ZIP 的大小和 SHA-256，再交给
`OfflineModelPackInstaller` 完成白名单解压、第二次逐文件校验和 active 指针原子切换。
取消或任一步失败都会 best-effort 清理本次下载目录；清理失败只写入 Debug 诊断，不会覆盖原始失败或已经成功的安装结果。仓库和安装包均不内置模型权重。

中文到英文固定使用 `onnx-community/opus-mt-zh-en` revision
`8e3032ebeebbacda779fe95efa64c03b962f83f3`：

| 远端文件 | 包内文件 | 字节数 | SHA-256 |
| --- | --- | ---: | --- |
| `onnx/decoder_model_quantized.onnx` | `decoder_model.onnx` | 192882669 | `6ec4ee5c028efb856e933d5d0732bac28f0914b34e652978fb201864931c49a6` |
| `onnx/encoder_model_quantized.onnx` | `encoder_model.onnx` | 52875078 | `86b0dc5a1d5d8062583800654864aae1311fce2172bba80910d02020d3693577` |
| `source.spm` | `source.spm` | 804677 | `e27a3a1b539f4959ec72ea60e453f49156289f95d4e6000b29332efc45616203` |
| `target.spm` | `target.spm` | 806530 | `6a881f4717cd7265f53fea54fd3dc689c767c05338fac7a4590f3088cb2d7855` |
| `vocab.json` | `vocab.json` | 1747906 | `08a119a1defd522fa047cb5e3bfe3e89633e96caa38ced0dc9cee7ef1021a011` |
| Crosio 内置 CC BY 4.0 完整文本 | `LICENSE.txt` | 18657 | `9ba9550ad48438d0836ddab3da480b3b69ffa0aac7b7878b5a0039e7ab429411` |

英文到中文固定使用 `Xenova/opus-mt-en-zh` revision
`046f55aec303cdee3e0318604406d4df20f1e8ea`：

| 远端文件 | 包内文件 | 字节数 | SHA-256 |
| --- | --- | ---: | --- |
| `onnx/decoder_model_quantized.onnx` | `decoder_model.onnx` | 59842102 | `2c66a3981099b40edbb3a0d65e015d05964d42fecb9780f8422776eff5939112` |
| `onnx/encoder_model_quantized.onnx` | `encoder_model.onnx` | 52899742 | `d3b7912bf6a9bd27e4c074c2df91d4ff3d5b4bc5f7f6c8d7cc9c805c98fbafee` |
| `source.spm` | `source.spm` | 806435 | `5775ddc9e3ff2fae91554da56468ad35ff56edaba870fea74447bc7234bfdaa8` |
| `target.spm` | `target.spm` | 804600 | `81dc94efa84e4025ef38d25d5d07429fe41e3eb29d44003f1db6fe98487b0052` |
| `vocab.json` | `vocab.json` | 1747795 | `22c957348eed495ee925afc40a36da3e387c8a34a734c8486967c2dca271613e` |
| Crosio 内置 Apache License 2.0 完整文本 | `LICENSE.txt` | 11358 | `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30` |

许可必须按每个固定 revision 单独处理：中文到英文模型为 CC-BY-4.0，英文到中文模型为
Apache-2.0，不能把两个方向概括成同一许可。包内保留完整许可文本与署名；运行界面应显示
manifest 的 `license.attribution`。

两个量化模型仓库在上述固定 revision 的文件树中均没有 `LICENSE` 文件。为避免安装流程依赖
Creative Commons 或 Apache 网站上未版本化的实时响应，Crosio 固定内置了 2026-09-06 从两家
许可发布方核验的完整文本。内置文本解压后仍必须匹配上表的精确字节数和 SHA-256，且只在生成
对应模型包时写入 `LICENSE.txt`；官方安装器不会为许可文本发起 HTTP 请求。

上游说明：

- <https://huggingface.co/Helsinki-NLP/opus-mt-zh-en>
- <https://huggingface.co/Helsinki-NLP/opus-mt-en-zh>
- <https://github.com/Helsinki-NLP/Opus-MT>

固定目录采用已发布的量化、非缓存 Marian encoder/decoder ONNX 图；更换 revision 或任何
文件时必须在代码和本说明中同时更新精确大小与 SHA-256，并经过真实推理回归。

## ZIP 结构

ZIP 根目录必须包含 `manifest.json`。除此之外，只允许出现 manifest 的 `artifacts`
数组逐项声明的文件。每项都必须带精确字节数和 SHA-256。典型内容如下：

```text
manifest.json
encoder_model.onnx
decoder_model.onnx
source.spm
target.spm
source-vocab.json
target-vocab.json
LICENSE.txt
```

ONNX 图必须使用 int64 `input_ids`/`attention_mask`，encoder 输出 float32 hidden state，
decoder 输出形状为 `[1, sequence_length, vocabulary_size]` 的 float32 logits。输入输出
名称可在 manifest 的 `graph` 字段中覆盖。

词表 JSON 是 `{ "piece": integerId }` 对象。SentencePiece 内部 ID 不必与 Marian
词表 ID 相同；运行时按 piece 字符串映射两个 ID 空间。上述两个官方包的 source/target
词表都指向同一个 `vocab.json`，`decodeWithSourceSentencePiece` 为 `false`。固定生成参数
为 `unk=1`、`eos=0`、`pad=decoderStart=65000`，并抑制词元 `65000`。

英文到中文的结果在 Windows 上还会经过本机 `LCMapStringEx` 的简体中文映射，不联网。

安装器先将 ZIP 流写入私有临时文件并验证目录提供的总大小与 SHA-256，再按白名单解压，
逐文件验证大小与哈希。完整模型目录通过同卷 `Directory.Move` 发布，最后用临时文件替换
对应方向的 active 指针；失败或取消不会切换当前模型。

调用示例：

```csharp
var catalog = new OfflineModelCatalog(modelRoot);
using var installer = new OfficialTranslationModelInstaller(catalog);
await installer.DownloadAndInstallAsync(TranslationDirection.ChineseToEnglish, progress, cancellationToken);
```
