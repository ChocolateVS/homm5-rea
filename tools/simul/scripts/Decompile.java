// Decompiles functions at the given addresses (or containing them) into one text file.
// Usage: -postScript Decompile.java <outFile> <addr> [addr...]   (addr file allowed with @path)
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import java.io.*;
import java.nio.file.*;
import java.util.*;

public class Decompile extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(args[0]));
        List<String> addrs = new ArrayList<>();
        for (int i = 1; i < args.length; i++) {
            if (args[i].startsWith("@")) addrs.addAll(Files.readAllLines(Paths.get(args[i].substring(1))));
            else addrs.add(args[i]);
        }
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        Listing listing = currentProgram.getListing();
        Set<Address> done = new HashSet<>();
        for (String s : addrs) {
            s = s.trim();
            if (s.isEmpty()) continue;
            Address a = toAddr(s);
            Function f = listing.getFunctionContaining(a);
            if (f == null) { createFunction(a, null); f = listing.getFunctionAt(a); }
            if (f == null) { out.println("// no function at " + s); continue; }
            if (!done.add(f.getEntryPoint())) continue;
            DecompileResults r = di.decompileFunction(f, 120, monitor);
            out.println("// ===== " + f.getName() + " @ " + f.getEntryPoint() + " (requested " + s + ")");
            out.println(r.decompileCompleted() ? r.getDecompiledFunction().getC() : "// decompile failed: " + r.getErrorMessage());
        }
        out.close();
    }
}
