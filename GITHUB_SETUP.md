# GitHub setup

1. Create a new **public** repository named `LightlessAutoPair`.
2. Do not initialize it with a README, license, or `.gitignore`.
3. Upload everything inside this folder to the repository root.
4. Make sure this file exists on GitHub:
   `.github/workflows/build-release.yml`
5. Open **Actions** → **Build and publish plugin** → **Run workflow**.
6. A successful run creates a GitHub release containing `latest.zip` and writes `pluginmaster.json` to the repository root.
7. Add the following URL to Dalamud's custom plugin repositories:

   `https://raw.githubusercontent.com/YOUR_GITHUB_USERNAME/LightlessAutoPair/main/pluginmaster.json`

Replace `YOUR_GITHUB_USERNAME` and the repository name if you choose a different one.
