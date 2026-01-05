import { Policy } from "../models/Policy";
import { BaseContract, Doken } from "./BaseContract";

export class GenericRealmAccessThresholdRoleContract extends BaseContract{
    protected validateData(policy: Policy): Promise<void> {
        throw new Error("validateData not implemented.");
    }
    public id: string = "GenericRealmAccessThresholdRole:1";
    protected async validateApprovers(policy: Policy, approverDokens: Doken[]): Promise<void> {
        let successfulDokens = 0;
        approverDokens.forEach(d => {
            if(d.hasRealmAccessRole(policy.params.getParameter<string>("role"))) successfulDokens++;
        })
        const threshold = policy.params.getParameter<number>("threshold");
        if(successfulDokens < threshold) throw 'Not enough successful dokens with requires roles/clients';
    }
}