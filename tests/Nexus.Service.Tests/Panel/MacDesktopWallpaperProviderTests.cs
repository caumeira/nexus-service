using System.Linq;
using System.Xml.Linq;
using Nexus.Service.Panel;
using Xunit;

namespace Nexus.Service.Tests.Panel;

public class MacDesktopWallpaperProviderTests
{
    // Captured verbatim from a real macOS 26 store
    // (~/Library/Application Support/com.apple.wallpaper/Store/Index.plist,
    // plutil -convert xml1) with an aerial selected, which links the desktop
    // and screen saver into one "Linked" scope and leaves Files empty.
    private const string AerialStore = """
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0">
        <dict>
          <key>AllSpacesAndDisplays</key>
          <dict>
            <key>Linked</key>
            <dict>
              <key>Content</key>
              <dict>
                <key>Choices</key>
                <array>
                  <dict>
                    <key>Configuration</key>
                    <data>YnBsaXN0MDDRAQJXYXNzZXRJRF8QJEEzM0E1NUQ5LUVERUEtNDU5Ni1BODUwLTZDMTBCNTRGQkJCNQ==</data>
                    <key>Files</key>
                    <array/>
                    <key>Provider</key>
                    <string>com.apple.wallpaper.choice.aerials</string>
                  </dict>
                </array>
                <key>Shuffle</key>
                <string>$null</string>
              </dict>
            </dict>
            <key>Type</key>
            <string>linked</string>
          </dict>
        </dict>
        </plist>
        """;

    [Fact]
    public void ActiveChoice_reads_the_linked_scope_used_by_an_aerial_selection()
    {
        var choice = MacDesktopWallpaperProvider.ActiveChoice(XDocument.Parse(AerialStore));

        Assert.NotNull(choice);
        Assert.Equal("com.apple.wallpaper.choice.aerials",
            MacDesktopWallpaperProvider.DictValue(choice, "Provider")?.Value);
    }

    [Fact]
    public void ActiveChoice_prefers_the_desktop_scope_over_the_linked_one()
    {
        var doc = XDocument.Parse("""
            <plist version="1.0"><dict>
              <key>AllSpacesAndDisplays</key><dict>
                <key>Desktop</key><dict><key>Content</key><dict><key>Choices</key><array>
                  <dict><key>Provider</key><string>desktop-scope</string></dict>
                </array></dict></dict>
                <key>Linked</key><dict><key>Content</key><dict><key>Choices</key><array>
                  <dict><key>Provider</key><string>linked-scope</string></dict>
                </array></dict></dict>
              </dict>
            </dict></plist>
            """);

        var choice = MacDesktopWallpaperProvider.ActiveChoice(doc);

        Assert.Equal("desktop-scope", MacDesktopWallpaperProvider.DictValue(choice, "Provider")?.Value);
    }

    [Fact]
    public void ActiveChoice_ignores_a_store_carrying_only_the_screen_saver_scope()
    {
        var doc = XDocument.Parse("""
            <plist version="1.0"><dict>
              <key>AllSpacesAndDisplays</key><dict>
                <key>Idle</key><dict><key>Content</key><dict><key>Choices</key><array>
                  <dict><key>Provider</key><string>screensaver</string></dict>
                </array></dict></dict>
              </dict>
            </dict></plist>
            """);

        Assert.Null(MacDesktopWallpaperProvider.ActiveChoice(doc));
    }

    [Fact]
    public void PicturePaths_yields_nothing_for_a_choice_that_names_no_file()
    {
        var choice = MacDesktopWallpaperProvider.ActiveChoice(XDocument.Parse(AerialStore))!;

        Assert.Empty(MacDesktopWallpaperProvider.PicturePaths(choice));
    }

    // Picture wallpapers are matched by shape rather than by key name: the
    // store has moved picture paths between keys across macOS releases, so the
    // scan takes any string that looks like an image path and the caller keeps
    // the first that exists on disk.
    [Fact]
    public void PicturePaths_takes_image_paths_and_file_urls_and_skips_everything_else()
    {
        var choice = XElement.Parse("""
            <dict>
              <key>Provider</key><string>com.apple.wallpaper.choice.image</string>
              <key>Files</key><array>
                <dict><key>relative</key><string>file:///Users/x/Pictures/My%20Shot.heic</string></dict>
              </array>
              <key>Extra</key><array>
                <string>/System/Library/Desktop Pictures/Sonoma.heic</string>
                <string>not-a-path</string>
                <string>/Users/x/notes.txt</string>
              </array>
            </dict>
            """);

        var paths = MacDesktopWallpaperProvider.PicturePaths(choice).ToList();

        Assert.Equal(new[]
        {
            "/Users/x/Pictures/My Shot.heic",
            "/System/Library/Desktop Pictures/Sonoma.heic",
        }, paths);
    }

    [Fact]
    public void DictValue_returns_the_element_following_the_key()
    {
        var dict = XElement.Parse("<dict><key>a</key><string>first</string><key>b</key><string>second</string></dict>");

        Assert.Equal("second", MacDesktopWallpaperProvider.DictValue(dict, "b")?.Value);
        Assert.Null(MacDesktopWallpaperProvider.DictValue(dict, "missing"));
    }
}
