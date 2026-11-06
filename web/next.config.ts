import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Production builds (deploy/publish.ps1 sets FOS_DIST) go to their own folder, so building never overwrites the
  // files of a running development server ("npm run dev" uses .next).
  distDir: process.env.FOS_DIST ?? ".next",
  // A self-contained server (node server.js) for on-premise installs and containers
  output: "standalone",
  // the hosted demo's recorded API answers, read at run time by src/app/api/v1/[...path]/route.ts
  outputFileTracingIncludes: { "/api/v1/[...path]": ["./demo-data/**"] },
};

export default nextConfig;
