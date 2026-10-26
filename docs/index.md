---
layout: home

hero:
  name: Layeron
  text: 体数据桌面工作台
  tagline: 切片浏览、掩膜编辑、C# / Python 脚本，以及一个可以扩展几乎一切的插件系统
  image:
    src: /logo.png
    alt: Layeron
  actions:
    - theme: brand
      text: 开始使用
      link: /guide/
    - theme: alt
      text: 插件开发
      link: /plugin/

features:
  - title: 三向切片浏览
    details: 轴位、冠状位、矢状位三个视图共享同一份数据与十字准线，配窗宽窗位与灰度直方图。
  - title: 大体积数据
    details: 以 64³ 块存储的 8 位 / 16 位体数据，读写走块级 API，不逐体素索引。
  - title: 掩膜编辑
    details: 稀疏位图掩膜图层，可叠加显示、独立配色与透明度，并可从掩膜生成 3D 模型。
  - title: 插件化
    details: 插件可以加工具面板、文档标签页、菜单、数据加载器与设置页面，热加载无残留；过滤器契约已就绪，等待宿主接入驱动。
  - title: C# / Python 脚本
    details: 用 .csx 脚本做一次性计算并安装为插件；用内嵌 CPython 的 REPL 与编辑器处理当前数据。
  - title: 可扩展的扩展点
    details: 插件不只向宿主贡献，还能自己发布扩展点，让其他插件来扩展它。
---
