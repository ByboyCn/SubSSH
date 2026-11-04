// SubSsh.Plugin.Memory — 记忆引擎公共组件插件（可插拔、可任意替换）。
// 全量移植 OneSSH internal/memoryx/engine.go + embed.go：
// Remember（同 bank 同内容去重、只升重要度）/ Update（内容变化重建 embedding）/
// Recall（FTS5 bm25 归一 + 可选向量余弦混合打分）/ Sleep（确定性维护：去重/衰减/清理）。
// 依赖：Require ISettings（EmbeddingApiUrl/Key/Model）、IOneSshStoreFull；Provide<IMemoryEngine>。
// EmbeddingApiUrl（或 Model）为空时走 Go 版 no-embedding 路径（纯 FTS，engine=fts/like）。
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.Memory;

[Plugin("memory", "记忆引擎：remember/recall/update/sleep，FTS+向量混合打分（可被同接口插件替换）")]
public sealed class MemoryPlugin : IComponent
{
    public string Name => "memory";

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        MemoryEngine? engine = null;
        // Require ISettings（Embedding* 决定是否启用向量召回）。
        ctx.Require<ISettings>(
            s =>
            {
                ctx.Require<IOneSshStoreFull>(
                    st =>
                    {
                        if (engine != null)
                        {
                            ctx.Provide<IMemoryEngine>(engine);
                            return;
                        }
                        engine = new MemoryEngine(st, s);
                        ctx.Provide<IMemoryEngine>(engine);
                    },
                    () => ctx.Unprovide<IMemoryEngine>());
            },
            () => ctx.Unprovide<IMemoryEngine>());
        return Task.CompletedTask;
    }
}
