using Helldivers2ModManager.Adapters;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModViewModelFactoryTests
{
    [TestMethod]
    public void GetOrCreate_ReusesWrapperForSameModGuid()
    {
        var directory = Directory.CreateTempSubdirectory("hd2mm-mod-viewmodel-factory-");
        try
        {
            var guid = Guid.NewGuid();
            var manifest = new LegacyModManifest
            {
                Guid = guid,
                Name = "Test Mod",
                Description = "Test",
                IconPath = null,
                Options = null
            };
            var factory = new ModViewModelFactory(mod => new ModViewModel(
                mod,
                NullLogger<ModService>.Instance,
                null!,
                null!,
                null!,
                null!));
            var first = new ModData(directory, manifest);
            var second = new ModData(directory, manifest);

            var firstViewModel = factory.GetOrCreate(first);
            var secondViewModel = factory.GetOrCreate(second);

            Assert.AreSame(firstViewModel, secondViewModel);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

