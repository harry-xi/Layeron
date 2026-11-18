# Layeron

一个用于体数据浏览与掩膜编辑的桌面工作台：三向切片视图、板块/稀疏掩膜、可热加载的插件系统，以及 C# / Python 脚本支持。

本项目主要是**个人实验与技术验证**，并非面向生产环境的成熟软件。

## 声明

- **这是一个 Demo 项目。** 功能、接口、目录结构和数据格式都可能随时变动，不保证向后兼容，也不适合直接用于任何正式用途。
- **项目中包含大量由 AI 撰写（或 AI 参与撰写）的代码。** 尽管有测试覆盖并且经过人工审阅和调整，仍可能存在设计不一致、隐藏缺陷或不符合最佳实践的写法。
- 请勿将其作为学习范本或生产依赖；使用风险自负，作者不提供任何形式的保证。

## 技术栈

- .NET 10 / C#
- Avalonia 12 + ReactiveUI + Dock
- 体数据存储与稀疏位图、插件加载（AssemblyLoadContext）、内嵌 CPython、ONNX Runtime

## 快速开始

需要 .NET 10 SDK，主要在 Windows 上开发与验证。

```powershell
dotnet run --project Layeron.App
```

运行测试：

```powershell
dotnet test
```

## 文档

- 使用指南：`docs/guide/`
- 插件开发：`docs/plugin/`
- 设计与实现记录：`design-notes/`

更多细节请直接阅读源码和上述文档。
