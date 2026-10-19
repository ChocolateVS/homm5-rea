// Finds instructions with an immediate operand (not a memory displacement) equal to given values.
// Usage: -postScript FindImm.java <out> <mnemonicPrefix|*> <hexImm> [...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.lang.OperandType;
import java.io.*;
import java.util.*;

public class FindImm extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        String pre = a[1];
        Set<Long> want = new HashSet<>();
        for (int i = 2; i < a.length; i++) want.add(Long.parseLong(a[i], 16));
        Listing l = currentProgram.getListing();
        for (Instruction ins : l.getInstructions(true)) {
            if (!pre.equals("*") && !ins.getMnemonicString().startsWith(pre)) continue;
            for (int op = 0; op < ins.getNumOperands(); op++) {
                int t = ins.getOperandType(op);
                if ((t & OperandType.SCALAR) == 0 || (t & OperandType.DYNAMIC) != 0) continue;
                Scalar s = ins.getScalar(op);
                if (s != null && want.contains(s.getUnsignedValue())) {
                    Function f = l.getFunctionContaining(ins.getAddress());
                    out.println(String.format("0x%x %s %-40s %s", s.getUnsignedValue(), ins.getAddress(), ins, f == null ? "<none>" : f.getName() + "@" + f.getEntryPoint()));
                }
            }
        }
        out.close();
    }
}
