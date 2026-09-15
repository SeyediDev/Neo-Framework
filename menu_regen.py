import json
from pathlib import Path
root=Path(r'E:\SJVS\Projects\Hyper\Backend')
admin=root/'src/AdminPanel/Hyper.AdminPanel.Domain'
catalog=json.loads((root/'docs/schema/sql-ui-catalog.json').read_text(encoding='utf-8'))
def q(v): return json.dumps(v, ensure_ascii=False)
def cat(r):
 c,n=r['category'],r['class'].lower()
 if c=='جداول موتور' or n.startswith('sqlact'): return 'ابزارهای فنی نئو'
 if c=='یکسان‌سازی' or n.startswith(('sqlexternal','sqlintegration','sqlinventoryreservation')): return 'اتصال و یکسان‌سازی'
 if n in {'sqltblshop','sqltblperson','sqltblshareholder','sqltblwarehouse'} or c in {'اشخاص','فروشگاه'}: return 'اطلاعات پایه مغازه'
 if c in {'محصول','محصول فروشگاه','محصول هایپریک','انبارداری'}: return 'کالا و موجودی'
 if c in {'حسابداری','حسابداری فروشگاه','خزانه داری','خزانه‌داری'}: return 'حسابداری و اسناد'
 if c in {'سفارش خرید','سفارش فروش'}: return 'خرید و فروش'
 return 'خدمات و تنظیمات'
groups={}
for r in catalog: groups.setdefault(cat(r),[]).append(r)
order=['اطلاعات پایه مغازه','کالا و موجودی','حسابداری و اسناد','خرید و فروش','اتصال و یکسان‌سازی','خدمات و تنظیمات','ابزارهای فنی نئو']
lines=['using Hyper.AdminPanel.Domain.UiDefinitions.HomePage;','using Hyper.Domain.Entities.Database;','namespace Hyper.AdminPanel.Domain.Domain;','public partial class HyperMenuDefinitions : MenuDefinition','{','    private void AddMenu_Hyper()','    {','        AddMenu<HomePageEntity, HomePageEntityUiDefinitions.HomePageDashboard>("داشبورد هایپریک", "home-dashboard");','        AddMenu("مغازه‌دار و اتصال", "building-organization", "MerchantSimulation", "Index");','        AddMenu("عملیات یکسان‌سازی", "activity-monitor", "MerchantSimulation", "Dashboard");']
for i,k in enumerate(order):
 if k not in groups: continue
 rows=groups[k]; lines += [f'        AddMenu({q(k)}, "database", "HyperCategory{i}", "");','        {','            StartSubMenus();']
 for r in rows: lines += [f'            AddMenu<{r["class"]}>({q(r["title"])}, "table");']
 lines += [f'            AddMenu("گزارش‌ها", "chart-bar", "HyperReports{i}", "");','            {','                StartSubMenus();']
 for r in rows: lines += [f'                AddReport<{r["class"]}>({q("گزارش "+r["title"])}, "chart-bar");']
 lines += ['                EndSubMenus();','            }','            EndSubMenus();','        }']
lines += ['    }','}']
(admin/'Menu/Menu_Hyper.cs').write_text('\n'.join(lines)+'\n',encoding='utf-8')
print('menu regenerated',len(catalog),'entities',len(groups),'categories')
