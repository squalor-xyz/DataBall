import os

def create_repository(directory='/home/jonathan/code/squalor/DataBall'):
    os.makedirs(directory, exist_ok=True)
    docs_dir = os.path.join(directory, 'docs')
    os.makedirs(docs_dir, exist_ok=True)

    all_files = {
        directory: {
            'LICENSE': '''Mozilla Public License Version 2.0\n==================================\n# Full MPL 2.0 text (copy from https://www.mozilla.org/en-US/MPL/2.0/)''',
            'DataBall.cs': '''# Full DataBall.cs code (copy from above)''',
            'DataBallTests.cs': '''# Full DataBallTests.cs code (copy from above)''',
            'NLog.config': '''# Full NLog.config (copy from previous response)''',
            'prompt_dump.json': '''# Full prompt_dump.json (copy from previous response)''',
            'DataBall.csproj': '''# Full DataBall.csproj (copy from previous response)''',
            '.gitignore': '''# Full .gitignore (copy from previous response)'''
        },
        docs_dir: {
            'DataBall_Class_Documentation.md': '''# DataBall Class Documentation\n# Full content (copy from previous response)''',
            'Row_Builder_Pattern_Details.md': '''# Row Builder Pattern Details\n# Full content (copy from previous response)''',
            'Proposal_for_Apache_Arrow_Integration.md': '''# Proposal for Apache Arrow Integration\n# Full content (copy from previous response)''',
            'Data_Backing_Proposal_for_DataBall.md': '''# Data Backing Proposal for DataBall\n# Full content (copy from previous response)''',
            'Apache_Arrow_Overview.md': '''# Apache Arrow Overview\n# Full content (copy from previous response)''',
            'Microsoft_Data_Analysis_DataFrame_Overview.md': '''# Microsoft.Data.Analysis DataFrame Overview\n# Full content (copy from previous response)''',
            'Tabular_Data_Libraries_in_NET_Performance_Insights.md': '''# Tabular Data Libraries in .NET: Performance Insights\n# Full content (copy from previous response)''',
            'Bounce_Operation_Details.md': '''# Bounce Operation Details\n# Full content (copy from previous response)''',
            'Proposal_for_Polars_Integration_in_DataBall.md': '''# Proposal for Polars Integration in DataBall\n# Full content (copy from previous response)''',
            'Proposal_for_Parquet_as_Data_Backing_in_DataBall.md': '''# Proposal for Parquet as Data Backing in DataBall\n# Full content (copy from previous response)'''
        }
    }

    for dir_path, dir_files in all_files.items():
        for filename, content in dir_files.items():
            with open(os.path.join(dir_path, filename), 'w', encoding='utf-8') as f:
                f.write(content.strip())

if __name__ == "__main__":
    create_repository()