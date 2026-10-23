import { defineConfig } from 'vitepress'

const repo = 'https://github.com/harry-xi/Layeron'

const zhGuide = [
  {
    text: '应用指南',
    items: [
      { text: '什么是 Layeron', link: '/guide/' },
      { text: '安装与运行', link: '/guide/getting-started' },
      { text: '界面与布局', link: '/guide/interface' },
      { text: '数据的加载与查看', link: '/guide/data' },
      { text: '掩膜图层', link: '/guide/masks' },
      { text: '标注图层', link: '/guide/annotations' },
      { text: '工作区', link: '/guide/workspace' },
      { text: 'C# 脚本', link: '/guide/scripting' }
    ]
  }
]

const zhPlugin = [
  {
    text: '插件开发',
    items: [
      { text: '插件系统总览', link: '/plugin/' },
      { text: '编写第一个插件', link: '/plugin/getting-started' },
      { text: '生命周期与作用域', link: '/plugin/lifecycle' },
      { text: '工具、文档与菜单', link: '/plugin/contributions' }
    ]
  },
  {
    text: '扩展宿主',
    items: [
      { text: '扩展点', link: '/plugin/extension-points' },
      { text: '宿主服务', link: '/plugin/services' },
      { text: '体数据过滤器', link: '/plugin/filters' },
      { text: '数据加载器', link: '/plugin/loaders' },
      { text: 'ONNX 模型运行器', link: '/plugin/onnx' },
      { text: '设置页面', link: '/plugin/settings' }
    ]
  },
  {
    text: '打包与进阶',
    items: [
      { text: '插件身份与元数据', link: '/plugin/metadata' },
      { text: '插件加载器', link: '/plugin/loader-plugins' },
      { text: 'API 参考', link: '/plugin/api-reference' }
    ]
  }
]

const enGuide = [
  {
    text: 'User Guide',
    items: [
      { text: 'What is Layeron', link: '/en/guide/' },
      { text: 'Install & Run', link: '/en/guide/getting-started' },
      { text: 'Interface & Layout', link: '/en/guide/interface' },
      { text: 'Loading & Viewing Data', link: '/en/guide/data' },
      { text: 'Mask Layers', link: '/en/guide/masks' },
      { text: 'Annotation Layers', link: '/en/guide/annotations' },
      { text: 'Workspaces', link: '/en/guide/workspace' },
      { text: 'C# Scripts', link: '/en/guide/scripting' }
    ]
  }
]

const enPlugin = [
  {
    text: 'Plugin Development',
    items: [
      { text: 'Overview', link: '/en/plugin/' },
      { text: 'Your First Plugin', link: '/en/plugin/getting-started' },
      { text: 'Lifetime & Scopes', link: '/en/plugin/lifecycle' },
      { text: 'Tools, Documents & Menus', link: '/en/plugin/contributions' }
    ]
  },
  {
    text: 'Extending the Host',
    items: [
      { text: 'Extension Points', link: '/en/plugin/extension-points' },
      { text: 'Host Services', link: '/en/plugin/services' },
      { text: 'Volume Filters', link: '/en/plugin/filters' },
      { text: 'Volume Loaders', link: '/en/plugin/loaders' },
      { text: 'ONNX Model Runner', link: '/en/plugin/onnx' },
      { text: 'Settings Pages', link: '/en/plugin/settings' }
    ]
  },
  {
    text: 'Packaging & Advanced',
    items: [
      { text: 'Identity & Metadata', link: '/en/plugin/metadata' },
      { text: 'Plugin Loaders', link: '/en/plugin/loader-plugins' },
      { text: 'API Reference', link: '/en/plugin/api-reference' }
    ]
  }
]

export default defineConfig({
  title: 'Layeron',
  description: '面向体数据的桌面工作台：切片浏览、掩膜编辑、C# 脚本与插件扩展',
  cleanUrls: true,
  lastUpdated: true,
  markdown: {
    lineNumbers: true
  },
  head: [['link', { rel: 'icon', href: '/logo.png' }]],
  themeConfig: {
    logo: '/logo.png',
    socialLinks: [{ icon: 'github', link: repo }]
  },
  locales: {
    root: {
      label: '简体中文',
      lang: 'zh-CN',
      themeConfig: {
        nav: [
          { text: '指南', link: '/guide/', activeMatch: '^/guide/' },
          { text: '插件开发', link: '/plugin/', activeMatch: '^/plugin/' }
        ],
        sidebar: {
          '/guide/': zhGuide,
          '/plugin/': zhPlugin
        },
        search: {
          provider: 'local',
          options: {
            translations: {
              button: { buttonText: '搜索文档', buttonAriaLabel: '搜索文档' },
              modal: {
                noResultsText: '没有找到结果',
                resetButtonTitle: '清除条件',
                footer: {
                  selectText: '选择',
                  navigateText: '切换',
                  closeText: '关闭'
                }
              }
            }
          }
        },
        outline: { level: [2, 3], label: '本页目录' },
        docFooter: { prev: '上一篇', next: '下一篇' },
        lastUpdated: {
          text: '最后更新于',
          formatOptions: { dateStyle: 'short', timeStyle: 'short' }
        },
        darkModeSwitchLabel: '外观',
        sidebarMenuLabel: '目录',
        returnToTopLabel: '回到顶部',
        langMenuLabel: '切换语言',
        editLink: {
          pattern: `${repo}/edit/master/docs/:path`,
          text: '在 GitHub 上编辑此页'
        },
        footer: {
          message: '基于 Avalonia 与 .NET 构建',
          copyright: 'Copyright © harry-xi'
        }
      }
    },
    en: {
      label: 'English',
      lang: 'en-US',
      link: '/en/',
      themeConfig: {
        nav: [
          { text: 'Guide', link: '/en/guide/', activeMatch: '^/en/guide/' },
          { text: 'Plugins', link: '/en/plugin/', activeMatch: '^/en/plugin/' }
        ],
        sidebar: {
          '/en/guide/': enGuide,
          '/en/plugin/': enPlugin
        },
        search: {
          provider: 'local',
          options: {
            translations: {
              button: { buttonText: 'Search', buttonAriaLabel: 'Search' },
              modal: {
                noResultsText: 'No results found',
                resetButtonTitle: 'Clear query',
                footer: {
                  selectText: 'select',
                  navigateText: 'navigate',
                  closeText: 'close'
                }
              }
            }
          }
        },
        outline: { level: [2, 3], label: 'On this page' },
        docFooter: { prev: 'Previous', next: 'Next' },
        lastUpdated: {
          text: 'Last updated',
          formatOptions: { dateStyle: 'short', timeStyle: 'short' }
        },
        darkModeSwitchLabel: 'Appearance',
        sidebarMenuLabel: 'Menu',
        returnToTopLabel: 'Return to top',
        langMenuLabel: 'Change language',
        editLink: {
          pattern: `${repo}/edit/master/docs/:path`,
          text: 'Edit this page on GitHub'
        },
        footer: {
          message: 'Built with Avalonia and .NET',
          copyright: 'Copyright © harry-xi'
        }
      }
    }
  }
})
