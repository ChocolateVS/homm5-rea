// Finds functions whose FPU/SSE memory operands use all given displacements.
// Usage: -postScript FindDispSet.java <outFile> <hexDisp>...
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.io.*;
import java.util.*;
public class FindDispSet extends GhidraScript {
    @Override public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Set<Long> want = new HashSet<>();
        for (int i = 1; i < a.length; i++) want.add(Long.parseLong(a[i], 16));
        Listing l = currentProgram.getListing();
        for (Function f : l.getFunctions(true)) {
            Set<Long> got = new HashSet<>();
            for (Instruction ins : l.getInstructions(f.getBody(), true)) {
                String m = ins.getMnemonicString();
                if (!(m.startsWith("F") || m.startsWith("MOVSS") || m.startsWith("MULSS") || m.startsWith("ADDSS"))) continue;
                for (int op = 0; op < ins.getNumOperands(); op++) {
                    for (Object o : ins.getOpObjects(op)) {
                        if (o instanceof Scalar s) { long v = s.getUnsignedValue(); if (want.contains(v)) got.add(v); }
                    }
                }
            }
            if (got.size() == want.size()) out.println(f.getEntryPoint() + " " + f.getName() + " size=" + f.getBody().getNumAddresses());
        }
        out.close();
    }
}
