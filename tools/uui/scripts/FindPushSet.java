// Finds functions that PUSH every immediate in the given set. Usage: FindPushSet.java <out> <hex>...
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.io.*;
import java.util.*;

public class FindPushSet extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Set<Long> want = new HashSet<>();
        for (int i = 1; i < a.length; i++) want.add(Long.parseLong(a[i], 16));
        for (Function f : currentProgram.getFunctionManager().getFunctions(true)) {
            Set<Long> got = new HashSet<>();
            for (Instruction ins : currentProgram.getListing().getInstructions(f.getBody(), true)) {
                if (!ins.getMnemonicString().equals("PUSH")) continue;
                Object[] o = ins.getOpObjects(0);
                if (o.length == 1 && o[0] instanceof Scalar) { long v = ((Scalar) o[0]).getUnsignedValue(); if (want.contains(v)) got.add(v); }
            }
            if (got.size() == want.size()) out.println(f.getEntryPoint() + " " + f.getName());
        }
        out.close();
    }
}
