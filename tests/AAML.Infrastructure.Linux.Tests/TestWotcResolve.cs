using System;
using System.IO;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Infrastructure.Linux.Launching;
using AAML.Infrastructure.Linux.Paths;
class P {
    static void Main() {
        var g = "/home/jake/.local/share/Steam/steamapps/common/XCOM 2";
        var physical = new LinuxPhysicalPathResolver();
        var known = new LinuxKnownArtifactResolver(physical);
        
        // Simulate HasNativeExecutable for WotC
        Console.WriteLine("=== HasNativeExecutable simulation ===");
        var r1 = known.ResolveExistingFile(g, "XCOM2WotC", "bin", "XCOM2WotC");
        Console.WriteLine("r1 (XCOM2WotC/bin/XCOM2WotC): " + r1.IsSuccess);
        
        var r2 = known.ResolveExistingFile(g, "XCom2-WarOfTheChosen", "Binaries", "Linux", "XCOM2WOTC");
        Console.WriteLine("r2 (XCom2-WarOfTheChosen/Binaries/Linux/XCOM2WOTC): " + r2.IsSuccess);
        
        var r3 = known.ResolveExistingFile(g, "XCom2-WarOfTheChosen", "XCOM2WOTC", "Binaries", "Linux", "XCOM2WOTC");
        Console.WriteLine("r3 (XCom2-WarOfTheChosen/XCOM2WOTC/Binaries/Linux/XCOM2WOTC): " + r3.IsSuccess);
        
        var r4 = known.ResolveExistingFile(g, "XCom2-WarOfTheChosen", "bin", "XCOM2WOTC");
        Console.WriteLine("r4 (XCom2-WarOfTheChosen/bin/XCOM2WOTC): " + r4.IsSuccess);
        
        // Check HasWindowsExecutable
        Console.WriteLine();
        Console.WriteLine("=== HasWindowsExecutable simulation ===");
        var w1 = known.ResolveExistingFile(g, "XCom2-WarOfTheChosen", "Binaries", "Win64", "XCom2.exe");
        Console.WriteLine("w1 (XCom2-WarOfTheChosen/Binaries/Win64/XCom2.exe): " + w1.IsSuccess);
        
        // The actual Resolve method
        Console.WriteLine();
        Console.WriteLine("=== Actual Resolve ===");
        var result = LinuxGameRuntimeLayout.Resolve(g, GameVariant.XCom2WarOfTheChosen, GameRuntime.Auto);
        Console.WriteLine("Resolve: " + result.IsSuccess);
        if (!result.IsSuccess) Console.WriteLine("Error: " + result.Error?.Message);
    }
}
