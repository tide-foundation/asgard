import { Policy } from "../models/Policy";
import { BaseContract } from "./BaseContract";

export class GenericResourceAccessThresholdRoleContract extends BaseContract{
    public id: string = "GenericResourceAccessThresholdRole:1";
    protected async test(policy: Policy): Promise<void> {
        let successfulDokens = 0;
        this.dokens.forEach(d => {
            if(d.hasResourceAccessRole(policy.params.getParameter<string>("role"), policy.params.getParameter<string>("resource"))) successfulDokens++;
        })
        const threshold = policy.params.getParameter<number>("threshold");
        if(successfulDokens < threshold) throw 'Not enough successful dokens with requires roles/clients';
    }
}