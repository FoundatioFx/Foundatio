import { defineConfig } from 'vitepress'
import llmstxt from 'vitepress-plugin-llms'

export default defineConfig({
  title: 'Foundatio',
  description: 'Pluggable foundation blocks for building loosely coupled distributed apps',
  base: '/',
  ignoreDeadLinks: true,
  vite: {
    plugins: [
      llmstxt({
        title: 'Foundatio Documentation',
        ignoreFiles: ['node_modules/**', '.vitepress/**']
      })
    ]
  },
  head: [
    ['link', { rel: 'icon', href: 'https://raw.githubusercontent.com/FoundatioFx/Foundatio/main/media/foundatio-icon.png', type: 'image/png' }],
    ['meta', { name: 'theme-color', content: '#3c8772' }]
  ],
  themeConfig: {
    logo: {
      light: 'https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio.svg',
      dark: 'https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio-dark-bg.svg'
    },
    siteTitle: false,
    nav: [
      { text: 'Guide', link: '/guide/what-is-foundatio' },
      { text: 'Mediator', link: 'https://mediator.foundatio.dev' },
      { text: 'GitHub', link: 'https://github.com/FoundatioFx/Foundatio' }
    ],
    sidebar: {
      '/guide/': [
        {
          text: 'Introduction',
          items: [
            { text: 'What is Foundatio?', link: '/guide/what-is-foundatio' },
            { text: 'Getting Started', link: '/guide/getting-started' },
            { text: 'Why Choose Foundatio?', link: '/guide/why-foundatio' }
          ]
        },
        {
          text: 'Core Abstractions',
          items: [
            { text: 'Caching', link: '/guide/caching' },
            { text: 'Queues', link: '/guide/queues' },
            { text: 'Locks', link: '/guide/locks' },
            { text: 'Messaging', link: '/guide/messaging' },
            { text: 'File Storage', link: '/guide/storage' },
            { text: 'Jobs', link: '/guide/jobs' }
          ]
        },
        {
          text: 'Advanced Topics',
          items: [
            { text: 'Configuration', link: '/guide/configuration' },
            { text: 'Dependency Injection', link: '/guide/dependency-injection' },
            { text: 'Resilience', link: '/guide/resilience' },
            { text: 'Serialization', link: '/guide/serialization' }
          ]
        },
        {
          text: 'Implementations',
          items: [
            { text: 'Aliyun', link: '/guide/implementations/aliyun' },
            { text: 'AWS', link: '/guide/implementations/aws' },
            { text: 'Azure', link: '/guide/implementations/azure' },
            { text: 'Hybrid Cache', link: '/guide/implementations/hybrid-cache' },
            { text: 'In-Memory', link: '/guide/implementations/in-memory' },
            { text: 'Kafka', link: '/guide/implementations/kafka' },
            { text: 'Minio', link: '/guide/implementations/minio' },
            { text: 'RabbitMQ', link: '/guide/implementations/rabbitmq' },
            { text: 'Redis', link: '/guide/implementations/redis' },
            { text: 'SshNet', link: '/guide/implementations/sshnet' }
          ]
        },
        {
          text: 'Related Projects',
          items: [
            { text: 'Foundatio.CommandQuery', link: 'https://github.com/FoundatioFx/Foundatio.CommandQuery' },
            { text: 'Foundatio.Lucene', link: 'https://lucene.foundatio.dev' },
            { text: 'Foundatio.Mediator', link: 'https://mediator.foundatio.dev' },
            { text: 'Foundatio.Parsers', link: 'https://github.com/FoundatioFx/Foundatio.Parsers' },
            { text: 'Foundatio.Repositories', link: 'https://github.com/FoundatioFx/Foundatio.Repositories' }
          ]
        }
      ]
    },
    socialLinks: [
      { icon: 'github', link: 'https://github.com/FoundatioFx/Foundatio' },
      { icon: 'discord', link: 'https://discord.gg/6HxgFCx' }
    ],
    footer: {
      message: 'Released under the Apache 2.0 License.',
      copyright: 'Copyright © 2025 Foundatio'
    },
    editLink: {
      pattern: 'https://github.com/FoundatioFx/Foundatio/edit/main/docs/:path'
    },
    search: {
      provider: 'local'
    }
  },
  markdown: {
    lineNumbers: false,
    codeTransformers: [
      {
        name: 'snippet-transformer',
        preprocess(code, options) {
          return code
        }
      }
    ]
  }
})
