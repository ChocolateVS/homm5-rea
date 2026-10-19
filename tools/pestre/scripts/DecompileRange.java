// Decompiles every function whose entry lies in [start, end].
// Usage: -postScript DecompileRange.java <outFile> <start> <end>
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import java.io.*;

public class DecompileRange extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        AddressSet set = new AddressSet(toAddr(a[1]), toAddr(a[2]));
        for (Function f : currentProgram.getListing().getFunctions(set, true)) {
            DecompileResults r = di.decompileFunction(f, 120, monitor);
            out.println("// ===== " + f.getName() + " @ " + f.getEntryPoint());
            out.println(r.decompileCompleted() ? r.getDecompiledFunction().getC() : "// failed");
        }
        out.close();
    }
}
