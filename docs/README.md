# Foundatio Documentation

This is the documentation site for [Foundatio](https://github.com/FoundatioFx/Foundatio), built with [VitePress](https://vitepress.dev).

## Prerequisites

- [Node.js](https://nodejs.org/) 18.x or higher
- npm (comes with Node.js)

## Getting Started

### Install Dependencies

```bash
npm install
```

### Development

Start the development server with hot-reload:

```bash
npm run docs:dev
```

The site will be available at `http://localhost:5173`.

### Build

Build the static site for production:

```bash
npm run docs:build
```

The built files will be in `.vitepress/dist`.

### Preview

Preview the production build locally:

```bash
npm run docs:preview
```

## Project Structure

```txt
docs/
├── .vitepress/
│   ├── config.ts          # VitePress configuration
│   └── dist/              # Built output (generated)
├── guide/
│   ├── what-is-foundatio.md
│   ├── getting-started.md
│   ├── why-foundatio.md
│   ├── caching.md
│   ├── queues.md
│   ├── locks.md
│   ├── messaging.md
│   ├── storage.md
│   ├── jobs.md
│   ├── resilience.md
│   ├── dependency-injection.md
│   ├── configuration.md
│   └── implementations/
│       ├── in-memory.md
│       ├── redis.md
│       ├── azure.md
│       └── aws.md
├── index.md               # Home page
├── package.json
└── README.md              # This file
```

## Writing Documentation

### Adding New Pages

1. Create a new `.md` file in the appropriate directory
2. Add frontmatter with title:

   ```yaml
   ---
   title: Your Page Title
   ---
   ```

3. Add the page to the sidebar in `.vitepress/config.ts`

### Code Blocks

Use triple backticks with language identifier:

````markdown
```csharp
var cache = new InMemoryCacheClient();
await cache.SetAsync("key", "value");
```
````

### Admonitions

VitePress supports custom containers:

```markdown
::: info
Informational message
:::

::: tip
Helpful tip
:::

::: warning
Warning message
:::

::: danger
Critical warning
:::
```

### Internal Links

Use relative paths for internal links:

```markdown
[Getting Started](./getting-started)
[Caching Guide](./caching.md)
```

## Plugins

This documentation site uses:

- **vitepress-plugin-llms**: Generates LLM-friendly documentation at `/llms.txt`
- **vitepress-plugin-mermaid**: Enables Mermaid diagrams in markdown

### Diagrams

The documentation uses diagrams for visual explanations:

```markdown
​```txt
┌─────────┐     ┌──────────────┐     ┌──────────────┐
│ Request │────▶│ Local Cache  │────▶│ Redis Cache  │
└─────────┘     └──────────────┘     └──────────────┘
                      │                      │
                      ▼                      ▼
                 Cache Hit?            Cache Hit?
​```
```

## Deployment

The documentation can be deployed to any static hosting service:

- GitHub Pages
- Netlify
- Vercel
- Azure Static Web Apps
- AWS S3 + CloudFront

### GitHub Pages

1. Build the site: `npm run docs:build`
2. Deploy `.vitepress/dist` to `gh-pages` branch

### Netlify / Vercel

Connect your repository and configure:

- Build command: `npm run docs:build`
- Output directory: `.vitepress/dist`

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Run the dev server to preview
5. Submit a pull request

## License

This documentation is part of the Foundatio project and is licensed under the same terms.
