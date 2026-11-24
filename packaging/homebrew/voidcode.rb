cask "voidcode" do
  arch arm: "osx-arm64", intel: "osx-x64"

  version "1.1.1"
  sha256 :no_check

  url "https://github.com/n3sty3/VoidCode/releases/download/v#{version}/VoidCode-#{version}-#{arch}.dmg"
  name "VoidCode"
  desc "Native, local-first AI IDE with BYOK freedom, Mission Control worktree agents, and Iced disassembler"
  homepage "https://github.com/n3sty3/VoidCode"

  livecheck do
    url :url
    strategy :github_latest
  end

  depends_on macos: ">= :monterey"

  app "VoidCode.app"
  binary "#{appdir}/VoidCode.app/Contents/Resources/bin/voidcode"

  zap trash: [
    "~/Library/Application Support/VoidCode",
    "~/Library/Caches/dev.voidcode.ide",
  ]
end
