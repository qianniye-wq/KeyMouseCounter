// 这些测试共用 AppPaths.RootOverride 这个静态重定向与 Log 的静态配置，
// 并行跑会互相踩临时目录，必须串行执行。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
