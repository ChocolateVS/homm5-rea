// Decompiles the first N slots of each vtable. Usage: VtDecomp.java <outFile> <n> <vt> [vt...]
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import java.io.*;
import java.util.*;

public class VtDecomp extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        int n = Integer.parseInt(a[1]);
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        Set<Address> done = new HashSet<>();
        for (int i = 2; i < a.length; i++) {
            Address base = toAddr(a[i]);
            out.println("######## VT " + base);
            for (int k = 0; k < n; k++) {
                long v = getInt(base.add(4L * k)) & 0xffffffffL;
                Address fa = toAddr(v);
                Function f = getFunctionContaining(fa);
                if (f == null) { try { disassemble(fa); createFunction(fa, null); } catch (Exception e) {} f = getFunctionAt(fa); }
                out.printf("## slot %d -> %08x%n", k, v);
                if (f == null || !done.add(f.getEntryPoint())) continue;
                DecompileResults r = di.decompileFunction(f, 120, monitor);
                out.println(r.decompileCompleted() ? r.getDecompiledFunction().getC() : "// fail");
            }
        }
        out.close();
    }
}
