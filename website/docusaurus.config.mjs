// design §20.7 (V1c1): the user documentation site. The pages live in docs/guide/ and read well on GitHub too.
// No analytics plugin (T28). Every broken link, Markdown link, or anchor fails the build.

/** @type {import('@docusaurus/types').Config} */
const config = {
  title: 'ElasticLM',
  tagline: 'Signed licenses, floating seats, and activation for .NET and C apps',
  url: 'https://yukikeda.github.io',
  baseUrl: '/ElasticLM/',
  // GitHub Pages serves each page as folder/index.html and adds the trailing slash (design §20.7, Issue #190 Q4).
  trailingSlash: true,
  onBrokenLinks: 'throw',
  onBrokenAnchors: 'throw',
  markdown: {
    // .md files are CommonMark, so the same files read well on GitHub; MDX is not used.
    format: 'detect',
    mermaid: true,
    hooks: {
      onBrokenMarkdownLinks: 'throw',
      onBrokenMarkdownImages: 'throw',
    },
  },
  // English is the source of truth; Japanese is its translation, changed in the same pull request (decision 0.117).
  i18n: {
    defaultLocale: 'en',
    locales: ['en', 'ja'],
    localeConfigs: {
      en: { label: 'English', htmlLang: 'en' },
      ja: { label: '日本語', htmlLang: 'ja' },
    },
  },
  presets: [
    [
      'classic',
      /** @type {import('@docusaurus/preset-classic').Options} */
      ({
        docs: {
          path: '../docs/guide',
          routeBasePath: '/',
          sidebarPath: './sidebars.js',
        },
        blog: false,
        pages: false,
        theme: {},
      }),
    ],
  ],
  themes: [
    '@docusaurus/theme-mermaid',
    [
      '@easyops-cn/docusaurus-search-local',
      {
        docsDir: '../docs/guide',
        docsRouteBasePath: '/',
        indexBlog: false,
        indexPages: false,
        language: ['en', 'ja'],
        hashed: true,
      },
    ],
  ],
  plugins: [
    // @docusaurus/theme-mermaid imports the optional @mermaid-js/layout-elk behind a flag that is false when the package
    // is missing, and webpack still resolves that import under pnpm. The ELK layout is not used, so it resolves to nothing.
    // Remove this plugin if @mermaid-js/layout-elk is ever installed: the flag would turn true and the import would be empty.
    () => ({
      name: 'elasticlm-no-mermaid-elk',
      configureWebpack: (_config, isServer) =>
        isServer ? {} : { resolve: { alias: { '@mermaid-js/layout-elk': false } } },
    }),
  ],
  themeConfig: {
    colorMode: { respectPrefersColorScheme: true },
    // The default Prism languages of prism-react-renderer leave out C#, Bash, and PowerShell, so their code blocks showed
    // as plain text.
    prism: { additionalLanguages: ['csharp', 'bash', 'powershell'] },
    navbar: {
      title: 'ElasticLM',
      items: [
        { type: 'localeDropdown', position: 'right' },
        { href: 'https://github.com/YUKIKEDA/ElasticLM', label: 'GitHub', position: 'right' },
      ],
    },
  },
};

export default config;
